// ReSharper disable UnusedMember.Local
// ReSharper disable PossibleNullReferenceException
// ReSharper disable MemberCanBeMadeStatic.Local
// ReSharper disable AssignNullToNotNullAttribute
// ReSharper disable CoVariantArrayConversion
// ReSharper disable ArrangeMethodOrOperatorBody
// ReSharper disable ArrangeConstructorOrDestructorBody

namespace Mira // 2
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Linq.Expressions;
  using System.Reflection;
  using System.Runtime.CompilerServices;
  using System.Threading;
  using System.Threading.Tasks;

  using Eto.Forms;

  using Microsoft.CSharp.RuntimeBinder;

  using Binder = Microsoft.CSharp.RuntimeBinder.Binder;

  public sealed class Mira
  {
    public sealed class CallEnvironment
    {
      public object CurrentItem { get; set; }
      public Items Items { get; set; }
    }

    public sealed class CallEnvironmentStack : Stack<CallEnvironment> { }

    public sealed class Items : List<object>
    {
      public Items() { }
      public Items(object instance) => Add(instance);
      public Items(IEnumerable<object> collection) : base(collection) { }

      public override string ToString() => "(" + string.Join(" ", ToArray()) + ")";
    }

    public sealed class ObjectStack : Stack<object>
    {
      public IEnumerable<object> Pop(int count) => Enumerable.Repeat<object>(null, count).Select(_ => Pop()).Reverse().ToArray();
    }

    public class OutputtingEventArgs : EventArgs
    {
      public int Target { get; set; }

      public string Message { get; set; }

    }

    public class ScopedDictionary<TK, TV> where TV : class
    {
      public sealed class Scope : Dictionary<TK, TV> { }
      private sealed class Scopes : Stack<Scope> { }

      private readonly Scopes scopes = new Scopes();

      protected ScopedDictionary() => EnterScope();

      public void Add(TK key, TV value) => scopes.Peek().Add(key, value);
      public bool ContainsKey(TK key) => TryGetValue(key, out _) != WordKind.None;
      public void EnterScope() => scopes.Push(new Scope());
      public TV Get(TK key) => TryGetValue(key, out TV result) == WordKind.None ? default : result;
      public void LeaveScope() => scopes.Pop();
      public void Set(TK key, TV value, bool local) => (local ? scopes.Peek() : scopes.Last())[key] = value;

      public WordKind TryGetValue(TK key, out TV value)
      {
        foreach (Scope currentScope in scopes)
        {
          if (currentScope.TryGetValue(key, out value))
          {
            return currentScope == scopes.Last() ? WordKind.Global : WordKind.Local;
          }
        }
        value = default;
        return WordKind.None;
      }
    }

    private sealed class Characters : Queue<char>
    {
      public Characters(IEnumerable<char> characters) : base(characters) { }
    }

    private sealed class CharHashSet : HashSet<char> { }

    public sealed class Name
    {
      public string Value { get; set; }
      public override bool Equals(object instance) => instance is Name name && Value == name.Value;
      public override int GetHashCode() => Value != null ? Value.GetHashCode() : 0;
      public override string ToString() => Value;
    }

    private sealed class StringHashSet : HashSet<string> { }

    private sealed class Tokens : Queue<object> { }

    private sealed class Words : ScopedDictionary<Name, object> { }

    public delegate void OutputtingEventHandler(object sender, OutputtingEventArgs arguments);

    public delegate void TerminatingEventHandler(object sender, Exception exception);

    public enum WordKind
    {
      None,
      Global,
      Local
    }

    private const char Eof = char.MinValue;
    private const char LeftParenthesis = '(';
    private const string LoadFilePragma = "loadFile";
    private const string NewMemberName = "new";
    private const string PragmaToken = "pragma";
    private const char Quote = '"';
    private const string ReferencePragma = "reference";
    private const char RightParenthesis = ')';
    private const char Whitespace = char.MaxValue;
    private readonly Application application;
    private readonly string baseDirectory;
    private readonly Name executeName = new Name { Value = "execute" };
    private readonly StringHashSet pragmas = new StringHashSet { LoadFilePragma, ReferencePragma };
    private readonly SemaphoreSlim semaphore = new SemaphoreSlim(0);
    private readonly Words setWords = new Words();
    private readonly CharHashSet stringStopCharacters = new CharHashSet { Eof, Quote };
    private readonly CharHashSet tokenStopCharacters = new CharHashSet { Eof, Quote, Whitespace, LeftParenthesis, RightParenthesis };
    private string code;
    private bool stepping;
    private Exception mostRecentExecutionException;

    public CallEnvironmentStack CallEnvironments { get; } = new CallEnvironmentStack();

    public string Code
    {
      get => code;
      set
      {
        code = value;
        Run(code, false);
      }
    }

    public Items CurrentRootItems { get; private set; }
    public bool Paused { get; private set; }
    public bool Running { get; private set; }
    public ObjectStack Stack { get; } = new ObjectStack();

    public Mira(Application application, string baseDirectory)
    {
      this.application = application;
      this.baseDirectory = baseDirectory;
      Reference("mscorlib, Version=4.0.0.0, Culture=neutral", "System", "System.Collections", "System.IO", "System.Reflection");
      setWords.Add(new Name { Value = "break" }, new Action(Break));
      setWords.Add(new Name { Value = "execute" }, new Action(Execute));
      setWords.Add(new Name { Value = "put" }, new Action(Put));
      setWords.Add(new Name { Value = "runtime" }, this);
      setWords.Add(new Name { Value = "set" }, new Action(Set));
    }

    public void Continue(bool step)
    {
      stepping = step;
      semaphore.Release();
    }

    public void Run() => Task.Factory.StartNew(() => Run(Code, true));

    private static string GetToken(Characters characters, CharHashSet stopCharacters)
    {
      string result = string.Empty;
      while (!stopCharacters.Contains(NextCharacter(characters)))
      {
        result += characters.Dequeue();
      }
      return result;
    }

    private static char NextCharacter(Characters characters)
    {
      char result = 0 < characters.Count ? characters.Peek() : char.MinValue;
      return result == Eof ? result : char.IsWhiteSpace(result) ? Whitespace : result;
    }

    private static object ToObject(object token)
    {
      if (int.TryParse(token.ToString(), out int intValue))
      {
        return intValue;
      }
      if (double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double doubleValue))
      {
        return doubleValue;
      }
      return new Name { Value = token.ToString() };
    }

    private void Break()
    {
      Paused = true;
      Invoke(() => Breaking?.Invoke());
      semaphore.Wait();
      Paused = false;
    }

    private void CreateDelegate()
    {
      if (Stack.Peek() is Type)
      {
        Stack.Push(CreateDelegate((Type) Stack.Pop(), (Items) Stack.Pop()));
        return;
      }
      EvaluateAndSplit();
      Stack.Push(CreateDelegate(Stack.Pop((int) Stack.Pop()).Cast<Type>(), (Type) Stack.Pop(), (Items) Stack.Pop()));
    }

    private Delegate CreateDelegate(Type delegateType, Items delegateCode) => CreateDelegate(delegateType, null, null, delegateCode);
    private Delegate CreateDelegate(IEnumerable<Type> parameterTypes, Type returnType, Items delegateCode) => CreateDelegate(typeof(void), parameterTypes, returnType, delegateCode);

    private Delegate CreateDelegate(Type delegateType, IEnumerable<Type> parameterTypes, Type returnType, Items delegateCode)
    {
      if (delegateType != typeof(void))
      {
        MethodInfo invokeMethod = delegateType.GetMethod("Invoke");
        parameterTypes = invokeMethod.GetParameters().Select(currentParameter => currentParameter.ParameterType).ToArray();
        returnType = invokeMethod.ReturnType;
      }
      ConstantExpression codeConstant = Expression.Constant(delegateCode);
      ConstantExpression thisConstant = Expression.Constant(this);
      ConstantExpression stackConstant = Expression.Constant(Stack);
      MethodInfo pushMethod = ((Action<object>) Stack.Push).Method;
      MethodInfo popMethod = ((Func<object>) Stack.Pop).Method;
      MethodInfo evaluateMethod = ((Action<object>) Evaluate).Method;
      ParameterExpression[] parameters = parameterTypes.Select(Expression.Parameter).ToArray();
      List<Expression> statements = new List<Expression>();
      statements.AddRange(parameters.Select(currentParameter => Expression.Call(stackConstant, pushMethod, Expression.Convert(currentParameter, typeof(object)))));
      statements.Add(Expression.Call(thisConstant, evaluateMethod, codeConstant));
      if (returnType != typeof(void))
      {
        statements.Add(Expression.Convert(Expression.Call(stackConstant, popMethod), returnType));
      }
      return (delegateType == typeof(void) ? Expression.Lambda(Expression.Block(statements), parameters) : Expression.Lambda(delegateType, Expression.Block(statements), parameters)).Compile();
    }

    private Exception DoExecute(Type type, string memberName, BindingFlags bindingFlags, object target, object[] arguments, bool hasReturnValue)
    {
      try
      {
        object invokeResult = type.InvokeMember(memberName, bindingFlags, null, target, arguments);
        if (hasReturnValue)
        {
          Stack.Push(invokeResult);
        }
        return null;
      }
      catch (Exception exception)
      {
        return exception;
      }
    }

    public void Evaluate(object item)
    {
      try
      {
        Items items = item as Items ?? new Items(item);
        CallEnvironments.Push(new CallEnvironment { Items = items });
        foreach (object currentItem in items)
        {
          CallEnvironments.Peek().CurrentItem = currentItem;
          Step();
          object word = null;
          switch (currentItem is Name key ? setWords.TryGetValue(key, out word) : WordKind.None)
          {
            case WordKind.Global:
              switch (word)
              {
                case Action actionValue:
                  actionValue.Invoke();
                  break;
                case Items itemsValue:
                  setWords.EnterScope();
                  Evaluate(itemsValue);
                  setWords.LeaveScope();
                  break;
                default:
                  Evaluate(word);
                  break;
              }
              break;
            case WordKind.Local:
              Stack.Push(word);
              break;
            default:
              Stack.Push(currentItem);
              break;
          }
        }
        CallEnvironments.Pop();
      }
      catch (Exception exception)
      {
        mostRecentExecutionException = exception;
        throw exception;
      }
    }

    private void Evaluate() => Evaluate(Stack.Pop());

    private void EvaluateAndSplit()
    {
      object items = Stack.Pop();
      int stackLength = Stack.Count;
      Evaluate(items);
      Stack.Push(Stack.Count - stackLength);
    }

    private void Execute()
    {
      string memberName = (string) Stack.Pop();
      int argumentsCount = 0;
      if (Stack.Peek() is Items)
      {
        EvaluateAndSplit();
        argumentsCount = (int) Stack.Pop();
      }
      object[] arguments = Stack.Pop(argumentsCount).ToArray();
      object typeOrTarget;
      object typeOrTargetOrInstanceFlag = Stack.Pop();
      if (typeOrTargetOrInstanceFlag is bool forceInstance)
      {
        typeOrTarget = Stack.Pop();
      }
      else
      {
        forceInstance = false;
        typeOrTarget = typeOrTargetOrInstanceFlag;
      }
      bool isType = !forceInstance && typeOrTarget is Type;
      Type type = isType ? (Type) typeOrTarget : typeOrTarget.GetType();
      object target = isType ? null : typeOrTarget;
      BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic;
      bool hasReturnValue = false;
      switch (memberName)
      {
        case NewMemberName:
          memberName = string.Empty;
          hasReturnValue = true;
          bindingFlags |= BindingFlags.Instance | BindingFlags.CreateInstance;
          break;
        default:
          bindingFlags |= BindingFlags.Static | BindingFlags.Instance;
          MemberInfo member = type.GetMember(memberName, bindingFlags | BindingFlags.Static).FirstOrDefault();
          if (member != null)
          {
            switch (member)
            {
              case MethodInfo methodInfo:
                hasReturnValue = methodInfo.ReturnType != typeof(void);
                bindingFlags |= BindingFlags.InvokeMethod;
                break;
              case FieldInfo _:
                hasReturnValue = arguments.Length == 0;
                bindingFlags |= hasReturnValue ? BindingFlags.GetField : BindingFlags.SetField;
                break;
              case PropertyInfo _:
                hasReturnValue = arguments.Length == 0;
                bindingFlags |= hasReturnValue ? BindingFlags.GetProperty : BindingFlags.SetProperty;
                break;
              case EventInfo eventInfo:
                memberName = eventInfo.AddMethod.Name;
                bindingFlags |= BindingFlags.InvokeMethod;
                break;
            }
          }
          break;
      }
      if (Invoke(() => DoExecute(type, memberName, bindingFlags, target, arguments, hasReturnValue)) is Exception exception)
      {
        mostRecentExecutionException = exception;
        throw exception;
      }
    }

    private void Get() => ((Items) Stack.Pop()).ForEach(currentItem => Stack.Push(setWords.Get((Name) currentItem)));

    private string GetCode(string codeOrFilename)
    {
      string path = baseDirectory + "/" + codeOrFilename;
      return File.Exists(path) ? File.ReadAllText(path) : codeOrFilename;
    }

    private Items GetItems(Tokens tokens)
    {
      Items result = new Items();
      while (0 < tokens.Count)
      {
        object currentToken = tokens.Dequeue();
        if (currentToken.ToString() == LeftParenthesis.ToString())
        {
          result.Add(GetItems(tokens));
        }
        else if (currentToken.ToString() == RightParenthesis.ToString())
        {
          break;
        }
        else
        {
          result.Add(currentToken);
        }
      }
      return result;
    }

    private Tokens GetTokens(string input)
    {
      Tokens result = new Tokens();
      Tokens currentPragmaTokens = new Tokens();
      Tokens currentTokens = result;
      Characters characters = new Characters(input.ToCharArray());
      for (char nextCharacter = NextCharacter(characters); nextCharacter != Eof; nextCharacter = NextCharacter(characters))
      {
        switch (nextCharacter)
        {
          case Whitespace:
            characters.Dequeue();
            break;
          case Quote:
            characters.Dequeue();
            currentTokens.Enqueue(GetToken(characters, stringStopCharacters));
            characters.Dequeue();
            break;
          case LeftParenthesis:
          case RightParenthesis:
            characters.Dequeue();
            currentTokens.Enqueue(ToObject(nextCharacter));
            break;
          default:
            string currentToken = GetToken(characters, tokenStopCharacters);
            if (pragmas.Contains(currentToken))
            {
              currentTokens = currentPragmaTokens;
              currentTokens.Enqueue(ToObject(currentToken));
            }
            else if (currentToken == PragmaToken)
            {
              currentTokens = result;
              HandlePragma(currentPragmaTokens);
              currentPragmaTokens.Clear();
            }
            else
            {
              currentTokens.Enqueue(ToObject(currentToken));
            }
            break;
        }
      }
      return result;
    }

    private void HandlePragma(Tokens pragmaTokens)
    {
      string pragma = ((Name) pragmaTokens.Dequeue()).Value;
      switch (pragma)
      {
        case LoadFilePragma:
          Evaluate(GetItems(GetTokens(GetCode(((Name) pragmaTokens.Dequeue()).Value))));
          break;
        case ReferencePragma:
          Reference((string) pragmaTokens.Dequeue(), (string) pragmaTokens.Dequeue());
          break;
      }
    }

    private void If()
    {
      object condition = Stack.Pop();
      object body = Stack.Pop();
      Evaluate(condition);
      if ((dynamic) Stack.Pop())
      {
        Evaluate(body);
      }
    }

    private void Invoke(Action action) => application.Invoke(action);
    private T Invoke<T>(Func<T> function) => application.Invoke(function);

    private void Join() => Stack.Push(new Items(Stack.Pop(Convert.ToInt32(Stack.Pop()))));

    private void MakeOperation()
    {
      object targetTypeOrParametersCount = Stack.Pop();
      if (targetTypeOrParametersCount is Type targetType)
      {
        Stack.Push(Operation(targetType));
        return;
      }
      Stack.Push(Operation(Stack.Pop(), (int) targetTypeOrParametersCount));
    }

    private Action Operation(object targetTypeOrExpressionType, int parametersCount = 0)
    {
      CSharpArgumentInfo[] GetParametersAndArgumentInfos(int count, out ParameterExpression[] parameterExpressions)
      {
        CSharpArgumentInfo argumentInfo = CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null);
        parameterExpressions = new ParameterExpression[count];
        CSharpArgumentInfo[] argumentInfos = new CSharpArgumentInfo[count];
        for (int i = 0; i < count; ++i)
        {
          parameterExpressions[i] = Expression.Parameter(typeof(object));
          argumentInfos[i] = argumentInfo;
        }
        return argumentInfos;
      }

      ParameterExpression[] parameters = null;
      CallSiteBinder binder = null;
      if (targetTypeOrExpressionType is Type targetType)
      {
        parametersCount = 1;
        GetParametersAndArgumentInfos(parametersCount, out parameters);
        binder = Binder.Convert(CSharpBinderFlags.ConvertExplicit, targetType, typeof(object));
      }
      else
      {
        targetType = typeof(object);
        ExpressionType expressionType = (ExpressionType) targetTypeOrExpressionType;
        switch (parametersCount)
        {
          case 1:
            binder = Binder.UnaryOperation(CSharpBinderFlags.None, expressionType, typeof(object), GetParametersAndArgumentInfos(parametersCount, out parameters));
            break;
          case 2:
            binder = Binder.BinaryOperation(CSharpBinderFlags.None, expressionType, typeof(object), GetParametersAndArgumentInfos(parametersCount, out parameters));
            break;
        }
      }
      Delegate function = Expression.Lambda(Expression.Dynamic(binder, targetType, parameters), parameters).Compile();
      return () =>
      {
        try
        {
          Stack.Push(function.DynamicInvoke(Stack.Pop(parametersCount).ToArray()));
        }
        catch (Exception exception)
        {
          mostRecentExecutionException = exception;
          throw exception;
        }
      };
    }

    private void Output() => Invoke(() => Outputting?.Invoke(this, new OutputtingEventArgs { Target = (int) Stack.Pop(), Message = Stack.Pop().ToString() }));
    private void Put() => ((Items) Stack.Pop()).AsEnumerable().Reverse().ToList().ForEach(currentItem => setWords.Set((Name) currentItem, Stack.Pop(), true));

    private void Reference(string assemblyName, params string[] requestedNamespaces)
    {
      foreach (Type currentType in Assembly.Load(assemblyName).GetTypes().Where(currentType => requestedNamespaces.Any(currentNamespace => currentType.Namespace == currentNamespace)))
      {
        setWords.Set(new Name { Value = currentType.Name }, currentType, false);
        foreach (MemberInfo currentMember in currentType.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
          bool accept = false;
          switch (currentMember)
          {
            case MethodInfo methodInfo:
              accept = !methodInfo.IsSpecialName;
              break;
            case FieldInfo fieldInfo:
              accept = !fieldInfo.IsSpecialName;
              break;
            case PropertyInfo propertyInfo:
              accept = !propertyInfo.IsSpecialName;
              break;
          }
          if (accept)
          {
            Name currentName = new Name { Value = currentMember.Name };
            if (!setWords.ContainsKey(currentName))
            {
              setWords.Add(currentName, new Items { currentMember.Name, executeName });
            }
          }
        }
      }
    }

    public Exception Run(string codeOrPath, bool evaluate)
    {
      try
      {
        Running = evaluate;
        CurrentRootItems = GetItems(codeOrPath);
        if (evaluate)
        {
          mostRecentExecutionException = null;
          Stack.Clear();
          CallEnvironments.Clear();
          Evaluate(CurrentRootItems);
          Terminate(mostRecentExecutionException);
        }
        return null;
      }
      catch (Exception exception)
      {
        Terminate(exception);
        return exception;
      }
    }

    public Items GetItems(string codeOrPath) => GetItems(GetTokens(GetCode(codeOrPath)));

    private void Set() => ((Items) Stack.Pop()).AsEnumerable().Reverse().ToList().ForEach(currentItem => setWords.Set((Name) currentItem, Stack.Pop(), false));
    private void Show() => Invoke(() => MessageBox.Show(Stack.Pop().ToString()));

    private void Split()
    {
      Items items = (Items) Stack.Pop();
      items.ForEach(currentItem => Stack.Push(currentItem));
      Stack.Push(items.Count);
    }

    private void Step()
    {
      if (stepping)
      {
        stepping = false;
        Paused = true;
        Invoke(() => Stepping?.Invoke());
        semaphore.Wait();
        Paused = false;
      }
    }

    private void Terminate(Exception exception)
    {
      Running = false;
      Invoke(() => Terminating?.Invoke(this, exception));
    }

    private void While()
    {
      object condition = Stack.Pop();
      object body = Stack.Pop();
      Evaluate(condition);
      while ((dynamic) Stack.Pop())
      {
        Evaluate(body);
        Evaluate(condition);
      }
    }

    public event Action Breaking;
    public event OutputtingEventHandler Outputting;
    public event Action Stepping;
    public event TerminatingEventHandler Terminating;
  }
}
