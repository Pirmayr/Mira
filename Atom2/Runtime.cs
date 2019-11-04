using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Eto.Forms;
using Microsoft.CSharp.RuntimeBinder;
using Binder = Microsoft.CSharp.RuntimeBinder.Binder;
using MessageBox = System.Windows.Forms.MessageBox;

#pragma warning disable 618

namespace Atom2
{
  public sealed class Runtime
  {
    private const char Apostrophe = '\'';
    private const char Eof = char.MinValue;
    private const char LeftAngle = '<';
    private const char LeftParenthesis = '(';
    private const string LoadFilePragma = "load-file";
    private const string MemberNameNew = "new";
    private const char Pipe = '|';
    private const string PragmaToken = "pragma";
    private const char Quote = '"';
    private const string ReferencePragma = "reference";
    private const char RightAngle = '>';
    private const char RightParenthesis = ')';
    private const char Whitespace = char.MaxValue;
    private readonly Name apostropheName = new Name {Value = Apostrophe.ToString()};
    private readonly NameHashSet blockBeginTokens;
    private readonly NameHashSet blockEndTokens;
    private readonly Name executeName = new Name {Value = "execute"};
    private readonly Name pipeName = new Name {Value = Pipe.ToString()};
    private readonly StringHashSet pragmas = new StringHashSet {LoadFilePragma, ReferencePragma};
    private readonly Words putWords = new Words();
    private readonly Words setWords = new Words();
    private readonly CharHashSet stringStopCharacters = new CharHashSet {Eof, Quote};
    private readonly CharHashSet tokenStopCharacters = new CharHashSet {Eof, Quote, Whitespace, LeftParenthesis, RightParenthesis, LeftAngle, RightAngle, Pipe, Apostrophe};
    public CallEnvironments CallEnvironments { get; } = new CallEnvironments();
    public Items CurrentRootItems { get; private set; }
    public Stack Stack { get; } = new Stack();
    private static string BaseDirectory { get; set; }
    private Application Application { get; set; }

    public Runtime(Application application, string baseDirectory)
    {
      Application = application;
      blockBeginTokens = NewNameHashSet(LeftParenthesis, LeftAngle, Pipe, Apostrophe);
      blockEndTokens = NewNameHashSet(RightParenthesis, RightAngle);
      BaseDirectory = baseDirectory;
      setWords.Add(new Name { Value = "trace" }, new Action(Trace));
      setWords.Add(new Name { Value = "output" }, new Action(Output));
      setWords.Add(new Name {Value = "show"}, new Action(Show));
      setWords.Add(new Name {Value = "break"}, new Action(Break));
      setWords.Add(new Name {Value = "execute"}, new Action(Execute));
      setWords.Add(new Name {Value = "put"}, new Action(Put));
      setWords.Add(new Name {Value = "set"}, new Action(Set));
      setWords.Add(new Name {Value = "get"}, new Action(Get));
      setWords.Add(new Name {Value = "if"}, new Action(If));
      setWords.Add(new Name {Value = "while"}, new Action(While));
      setWords.Add(new Name {Value = "evaluate"}, new Action(Evaluate));
      setWords.Add(new Name {Value = "split"}, new Action(Split));
      setWords.Add(new Name {Value = "evaluate-and-split"}, new Action(EvaluateAndSplit));
      setWords.Add(new Name {Value = "join"}, new Action(Join));
      setWords.Add(new Name {Value = "cast"}, new Action(Cast));
      setWords.Add(new Name {Value = "create-event-handler"}, new Action(CreateEventHandler));
      setWords.Add(new Name {Value = "Runtime"}, typeof(Runtime));
      setWords.Add(new Name {Value = "runtime"}, this);
      setWords.Add(new Name {Value = "to-name"}, new Action(ToName));
      setWords.Add(new Name {Value = "make-binary-action"}, new Action(MakeBinaryAction));
      setWords.Add(new Name {Value = "make-unary-action"}, new Action(MakeUnaryAction));
      Reference("mscorlib, Version=4.0.0.0, Culture=neutral", "System.Reflection");
      Reference("mscorlib, Version=4.0.0.0, Culture=neutral", "System");
      Reference("System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", "System.Linq.Expressions");
    }

    private void Output()
    {
      Application.Invoke(DoOutput);
    }

    private void DoOutput()
    {
      Outputting.Invoke(this, Pop().ToString());
    }

    public static string Code(string codeOrFilename)
    {
      string path = BaseDirectory + "/" + codeOrFilename;
      return File.Exists(path) ? File.ReadAllText(path) : codeOrFilename;
    }

    public Exception Run(string codeOrPath, bool evaluate, bool isOutermostRun)
    {
      try
      {
        CurrentRootItems = GetItems(GetTokens(Code(codeOrPath)));
        if (evaluate)
        {
          if (isOutermostRun)
          {
            Stack.Clear();
            CallEnvironments.Clear();
          }
          Evaluate(CurrentRootItems);
        }
        if (isOutermostRun)
        {
          InvokeTerminating(null);
        }
        return null;
      }
      catch (Exception exception)
      {
        if (isOutermostRun)
        {
          InvokeTerminating(exception);
        }
        return exception;
      }
    }

    private static string GetCall(Type type, string memberName, object[] arguments)
    {
      string typeName = type == null ? "" : type.Name;
      string argumentsString = arguments == null ? "" : string.Join(", ", arguments);
      return typeName + "." + memberName + "(" + argumentsString + ")";
    }

    private static string GetToken(Characters characters, CharHashSet stopCharacters)
    {
      string result = "";
      while (!stopCharacters.Contains(NextCharacter(characters)))
      {
        result += characters.Dequeue();
      }
      return result;
    }

    private static NameHashSet NewNameHashSet(params object[] arguments)
    {
      NameHashSet result = new NameHashSet();
      foreach (object currentArgument in arguments)
      {
        result.Add(new Name {Value = currentArgument.ToString()});
      }
      return result;
    }

    private static char NextCharacter(Characters characters)
    {
      char result = (0 < characters.Count) ? characters.Peek() : char.MinValue;
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
      return new Name {Value = token.ToString()};
    }

    private Action BinaryAction(ExpressionType expressionType)
    {
      Type objectType = typeof(object);
      ParameterExpression parameterA = Expression.Parameter(objectType);
      ParameterExpression parameterB = Expression.Parameter(objectType);
      CSharpArgumentInfo argumentInfo = CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null);
      CSharpArgumentInfo[] argumentInfos = {argumentInfo, argumentInfo};
      CallSiteBinder binder = Binder.BinaryOperation(CSharpBinderFlags.None, expressionType, objectType, argumentInfos);
      DynamicExpression expression = Expression.Dynamic(binder, objectType, parameterB, parameterA);
      LambdaExpression lambda = Expression.Lambda(expression, parameterA, parameterB);
      Delegate function = lambda.Compile();
      return delegate { Push(function.DynamicInvoke(Pop(), Pop())); };
    }

    private void Break()
    {
      Breaking?.Invoke();
    }

    private void Cast()
    {
      Type type = (Type) Pop();
      object instance = Pop();
      Push(Expression.Lambda(Expression.Convert(Expression.Constant(instance), type)).Compile().DynamicInvoke());
    }

    private void CreateEventHandler()
    {
      Items items = (Items) Pop();
      EventHandler action = (sender, eventArguments) => EventHandler(items, sender, eventArguments);
      Push(action);
    }

    private Exception DoExecute()
    {
      string memberName = "(unknown)";
      object[] arguments = null;
      Type type = null;
      try
      {
        memberName = (string) Pop();
        int argumentsCount = (int) Pop();
        arguments = Pop(argumentsCount).ToArray();
        object typeOrTarget = Pop();
        bool isType = typeOrTarget is Type;
        type = isType ? (Type) typeOrTarget : typeOrTarget.GetType();
        object target = isType ? null : typeOrTarget;
        BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic;
        bool hasReturnValue = false;
        switch (memberName)
        {
          case MemberNameNew:
            memberName = "";
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
                  bindingFlags |= (hasReturnValue ? BindingFlags.GetField : BindingFlags.SetField);
                  break;
                case PropertyInfo _:
                  hasReturnValue = arguments.Length == 0;
                  bindingFlags |= (hasReturnValue ? BindingFlags.GetProperty : BindingFlags.SetProperty);
                  break;
                case EventInfo eventInfo:
                  memberName = eventInfo.AddMethod.Name;
                  bindingFlags |= BindingFlags.InvokeMethod;
                  break;
              }
            }
            break;
        }
        object invokeResult = type.InvokeMember(memberName, bindingFlags, null, target, arguments);
        if (hasReturnValue)
        {
          Push(invokeResult);
        }
        return null;
      }
      catch (Exception exception)
      {
        return new Exception($"Cannot execute '{GetCall(type, memberName, arguments)}'", exception);
      }
    }

    private void DoInvokeTerminating(Exception exception)
    {
      Terminating?.Invoke(this, exception);
    }

    private void DoShow()
    {
      MessageBox.Show(Pop().ToString());
    }

    private void DoTrace()
    {
      Outputting?.Invoke(this, Stack.Peek().ToInformation());
    }

    private void Evaluate(object item)
    {
      Items items = item.ToItems();
      CallEnvironments.Push(new CallEnvironment {Items = items, Scope = putWords.CurrentScope});
      foreach (object currentItem in items)
      {
        CallEnvironments.Peek().CurrentItem = currentItem;
        Stepping?.Invoke();
        switch (TryGetWord(currentItem, out object word))
        {
          case WordKind.Set:
            switch (word)
            {
              case Action actionValue:
                actionValue.Invoke();
                break;
              case Items itemsValue:
                putWords.EnterScope();
                Evaluate(itemsValue);
                putWords.LeaveScope();
                break;
              default:
                Evaluate(word);
                break;
            }
            break;
          case WordKind.Put:
            Push(word);
            break;
          default:
            Push(currentItem);
            break;
        }
      }
      CallEnvironments.Pop();
    }

    private void Evaluate()
    {
      Evaluate(Pop());
    }

    private void EvaluateAndSplit()
    {
      object items = Pop();
      int stackLength = Stack.Count;
      Evaluate(items);
      Push(Stack.Count - stackLength);
    }

    private void EventHandler(Items items, object sender, EventArgs eventArguments)
    {
      Push(sender);
      Push(eventArguments);
      Evaluate(items);
    }

    private void Execute()
    {
      object memberName = Pop();
      if (Stack.Peek() is Items)
      {
        EvaluateAndSplit();
      }
      else
      {
        Push(0);
      }
      Push(memberName);
      if (Application.Invoke(DoExecute) is Exception exception)
      {
        throw exception;
      }
    }

    private void Get()
    {
      foreach (Name currentKey in ((Items) Pop()).Select(currentItem => (Name) currentItem))
      {
        Push(putWords.ContainsKey(currentKey) ? putWords[currentKey] : setWords[currentKey]);
      }
    }

    private Items GetItems(Tokens tokens)
    {
      Items result = new Items();
      while (0 < tokens.Count)
      {
        object currentToken = tokens.Dequeue();
        if (blockBeginTokens.Contains(currentToken))
        {
          OnBlockBegin(currentToken);
          result.Add(GetItems(tokens));
        }
        else if (blockEndTokens.Contains(currentToken))
        {
          OnBlockEnd(currentToken);
          break;
        }
        else
        {
          result.Add(currentToken);
        }
      }
      return result;
    }

    private Tokens GetTokens(string code)
    {
      Tokens result = new Tokens();
      Tokens currentPragmaTokens = new Tokens();
      Tokens currentTokens = result;
      Characters characters = new Characters(code.ToCharArray());
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
          case LeftAngle:
          case RightAngle:
          case Pipe:
          case Apostrophe:
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

    private void HandlePragma(Tokens tokens)
    {
      string pragma = ((Name) tokens.Dequeue()).Value;
      switch (pragma)
      {
        case LoadFilePragma:
          if (Run(((Name) tokens.Dequeue()).Value, true, false) is Exception exception)
          {
            throw exception;
          }
          break;
        case ReferencePragma:
          Reference((string) tokens.Dequeue(), (string) tokens.Dequeue());
          break;
      }
    }

    private void If()
    {
      object condition = Pop();
      object body = Pop();
      Evaluate(condition);
      if ((dynamic) Pop())
      {
        Evaluate(body);
      }
    }

    private void InvokeTerminating(Exception exception)
    {
      Application.Invoke(() => DoInvokeTerminating(exception));
    }

    private void Join()
    {
      Push(new Items(Pop((int) Pop())));
    }

    private void Length()
    {
      Push(((Items) Pop()).Count);
    }

    private void MakeBinaryAction()
    {
      Push(BinaryAction((ExpressionType) Pop()));
    }

    private void MakeUnaryAction()
    {
      Push(UnaryAction((ExpressionType) Pop()));
    }

    private void OnBlockBegin(object token)
    {
      if (token is Name name)
      {
        if (name.Equals(pipeName))
        {
          blockBeginTokens.Remove(pipeName);
          blockEndTokens.Add(pipeName);
        }
        else if (name.Equals(apostropheName))
        {
          blockBeginTokens.Remove(apostropheName);
          blockEndTokens.Add(apostropheName);
        }
      }
    }

    private void OnBlockEnd(object token)
    {
      if (token is Name name)
      {
        if (name.Equals(pipeName))
        {
          blockEndTokens.Remove(pipeName);
          blockBeginTokens.Add(pipeName);
        }
        else if (name.Equals(apostropheName))
        {
          blockEndTokens.Remove(apostropheName);
          blockBeginTokens.Add(apostropheName);
        }
      }
    }

    private IEnumerable<object> Pop(int count)
    {
      object[] result = new object[count];
      for (int i = count - 1; 0 <= i; --i)
      {
        result[i] = Stack.Pop();
      }
      return result;
    }

    private object Pop()
    {
      return Stack.Pop();
    }

    private void Push(object item)
    {
      Stack.Push(item);
    }

    private void Put()
    {
      foreach (Name currentItem in Enumerable.Reverse((Items) Pop()))
      {
        putWords[currentItem] = Pop();
      }
    }

    private void Reference(string assemblyName, string requestedNamespace)
    {
      HashSet<string> names = new HashSet<string>();
      foreach (Type currentType in Assembly.Load(assemblyName).GetTypes())
      {
        if (currentType.Namespace == requestedNamespace)
        {
          setWords[new Name {Value = currentType.Name}] = currentType;
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
              names.Add(currentMember.Name);
            }
          }
        }
      }
      foreach (string currentName in names)
      {
        Name newName = new Name {Value = currentName};
        if (!setWords.ContainsKey(newName))
        {
          setWords.Add(newName, new Items {currentName, executeName});
        }
      }
    }

    private void Set()
    {
      foreach (Name currentItem in Enumerable.Reverse((Items) Pop()))
      {
        setWords[currentItem] = Pop();
      }
    }

    private void Show()
    {
      Application.Invoke(DoShow);
    }

    private void Split()
    {
      object item = Pop();
      Items items = (Items) item;
      foreach (object currentItem in items)
      {
        Push(currentItem);
      }
      Push(items.Count);
    }

    private void ToName()
    {
      Push(new Name {Value = Pop().ToString()});
    }

    private void Trace()
    {
      Application.Invoke(DoTrace);
    }

    private WordKind TryGetWord(object item, out object word)
    {
      if (item is Name key)
      {
        if (putWords.TryGetValue(key, out word))
        {
          return WordKind.Put;
        }
        if (setWords.TryGetValue(key, out word))
        {
          return WordKind.Set;
        }
      }
      word = null;
      return WordKind.None;
    }

    private Action UnaryAction(ExpressionType expressionType)
    {
      Type objectType = typeof(object);
      ParameterExpression parameter = Expression.Parameter(objectType);
      CSharpArgumentInfo argumentInfo = CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null);
      CSharpArgumentInfo[] argumentInfos = {argumentInfo};
      CallSiteBinder binder = Binder.UnaryOperation(CSharpBinderFlags.None, expressionType, objectType, argumentInfos);
      DynamicExpression expression = Expression.Dynamic(binder, objectType, parameter);
      LambdaExpression lambda = Expression.Lambda(expression, parameter);
      Delegate function = lambda.Compile();
      return delegate { Push(function.DynamicInvoke(Pop())); };
    }

    private void While()
    {
      object condition = Pop();
      object body = Pop();
      Evaluate(condition);
      while ((dynamic) Pop())
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