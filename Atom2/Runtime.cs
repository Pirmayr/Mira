﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CSharp.RuntimeBinder;
using Binder = Microsoft.CSharp.RuntimeBinder.Binder;

// ReSharper disable ParameterOnlyUsedForPreconditionCheck.Local
#pragma warning disable 618

namespace Atom2
{
  using Tokens = Queue<string>;
  using CharHashSet = HashSet<char>;

  public sealed class Runtime
  {
    private sealed class Items : List<object>
    {
    }

    private sealed class Stack : Stack<object>
    {
      public object[] Pop(int count)
      {
        object[] result = new object[count];
        for (int i = count - 1; 0 <= i; --i)
        {
          result[i] = Pop();
        }
        return result;
      }
    }

    private sealed class Words : ScopedDictionary<string, object>
    {
    }

    private const char Eof = char.MinValue;
    private const char Whitespace = char.MaxValue;
    private readonly Stack stack = new Stack();
    private readonly CharHashSet stringStopCharacters = new CharHashSet { Eof, '"' };
    private readonly CharHashSet tokenStopCharacters = new CharHashSet { Eof, Whitespace, '(', ')', '[', ']', '{', '}', '<', '>', '"' };
    private readonly Words words = new Words();

    public Runtime()
    {
      words.Add("invoke", new Action(Invoke));
      words.Add("equal", BinaryAction(ExpressionType.Equal));
      words.Add("not-equal", BinaryAction(ExpressionType.NotEqual));
      words.Add("less-or-equal", BinaryAction(ExpressionType.LessThanOrEqual));
      words.Add("less", BinaryAction(ExpressionType.LessThan));
      words.Add("add", BinaryAction(ExpressionType.Add));
      words.Add("subtract", BinaryAction(ExpressionType.Subtract));
      words.Add("set", new Action(Set));
      words.Add("get", new Action(Get));
      words.Add("if", new Action(If));
      words.Add("while", new Action(While));
      words.Add("evaluate", new Action(Evaluate));
      words.Add("length", new Action(Length));
      words.Add("split", new Action(Split));
      words.Add("left-angle", new Action(LeftAngle));
      words.Add("left-brace", new Action(LeftBrace));
      words.Add("left-bracket", new Action(LeftBracket));
      words.Add("left-parenthesis", new Action(LeftParenthesis));
      words.Add("right-angle", new Action(RightAngle));
      words.Add("right-brace", new Action(RightBrace));
      words.Add("right-bracket", new Action(RightBracket));
      words.Add("right-parenthesis", new Action(RightParenthesis));
      words.Add("post-left-parenthesis", new Action(PostLeftParenthesis));
      words.Add("pre-right-parenthesis", new Action(PreRightParenthesis));
      words.Add("post-left-angle", new Action(PostLeftAngle));
      words.Add("pre-right-angle", new Action(PreRightAngle));
    }

    public void Run(string code)
    {
      Evaluate(GetItems(GetTokens(code), null, out _));
    }

    private static Items GetItems(Tokens tokens, string firstToken, out string lastToken)
    {
      lastToken = null;
      Items result = new Items();
      if (firstToken == "<")
      {
        result.Add("post-left-angle");
      }
      while (0 < tokens.Count)
      {
        string currentToken = tokens.Dequeue();
        lastToken = currentToken;
        if (currentToken == "(")
        {
          result.Add("left-parenthesis");
          Items currentItems = GetItems(tokens, currentToken, out string currentLastToken);
          result.Add(currentItems);
          if (currentLastToken == ")")
          {
            result.Add("right-parenthesis");
          }
        }
        else if (currentToken == "[")
        {
          result.Add("left-bracket");
          Items currentItems = GetItems(tokens, currentToken, out string currentLastToken);
          result.Add(currentItems);
          if (currentLastToken == "]")
          {
            result.Add("right-bracket");
          }
        }
        else if (currentToken == "{")
        {
          result.Add("left-brace");
          Items currentItems = GetItems(tokens, currentToken, out string currentLastToken);
          result.Add(currentItems);
          if (currentLastToken == "}")
          {
            result.Add("right-brace");
          }
        }
        else if (currentToken == "<")
        {
          result.Add("left-angle");
          Items currentItems = GetItems(tokens, currentToken, out string currentLastToken);
          result.Add(currentItems);
          if (currentLastToken == ">")
          {
            result.Add("right-angle");
          }
        }
        else if (currentToken == ")")
        {
          break;
        }
        else if (currentToken == "]")
        {
          break;
        }
        else if (currentToken == "}")
        {
          break;
        }
        else if (currentToken == ">")
        {
          result.Add("pre-right-angle");
          break;
        }
        else
        {
          result.Add(ToObject(currentToken));
        }
      }
      return result;
    }

    private static string GetToken(Queue<char> characters, HashSet<char> stopCharacters)
    {
      string result = "";
      while (!stopCharacters.Contains(NextCharacter(characters)))
      {
        result += characters.Dequeue();
      }
      return result;
    }

    private static char NextCharacter(Queue<char> characters)
    {
      char result = (0 < characters.Count) ? characters.Peek() : char.MinValue;
      return result == Eof ? result : char.IsWhiteSpace(result) ? Whitespace : result;
    }

    private static object ToObject(string token)
    {
      if (int.TryParse(token, out int intValue))
      {
        return intValue;
      }
      if (double.TryParse(token, out double doubleValue))
      {
        return doubleValue;
      }
      if (token.StartsWith("\"", StringComparison.Ordinal))
      {
        return token.Substring(1);
      }
      return token;
    }

    private Action BinaryAction(ExpressionType expressionType)
    {
      Type objectType = typeof(object);
      ParameterExpression parameterA = Expression.Parameter(objectType);
      ParameterExpression parameterB = Expression.Parameter(objectType);
      CSharpArgumentInfo argumentInfo = CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null);
      CSharpArgumentInfo[] argumentInfos = { argumentInfo, argumentInfo };
      CallSiteBinder binder = Binder.BinaryOperation(CSharpBinderFlags.None, expressionType, objectType, argumentInfos);
      DynamicExpression expression = Expression.Dynamic(binder, objectType, parameterB, parameterA);
      LambdaExpression lambda = Expression.Lambda(expression, parameterA, parameterB);
      Delegate function = lambda.Compile();
      return () => { stack.Push(function.DynamicInvoke(stack.Pop(), stack.Pop())); };
    }

    private void Evaluate(object unit)
    {
      if (unit is Items list)
      {
        list.ForEach(Process);
        return;
      }
      Process(unit);
    }

    private void Evaluate()
    {
      Evaluate(stack.Pop());
    }

    private void Get()
    {
      foreach (object currentItem in (Items)stack.Pop())
      {
        stack.Push(words[currentItem.ToString()]);
      }
    }

    private Tokens GetTokens(string code)
    {
      Tokens result = new Tokens();
      Queue<char> characters = new Queue<char>(code.ToCharArray());
      for (char nextCharacter = NextCharacter(characters); nextCharacter != Eof; nextCharacter = NextCharacter(characters))
      {
        switch (nextCharacter)
        {
          case '(':
          case ')':
          case '[':
          case ']':
          case '{':
          case '}':
          case '<':
          case '>':
            result.Enqueue(nextCharacter.ToString());
            characters.Dequeue();
            break;
          case '"':
            characters.Dequeue();
            result.Enqueue('"' + GetToken(characters, stringStopCharacters));
            characters.Dequeue();
            break;
          default:
            if (nextCharacter == Whitespace)
            {
              characters.Dequeue();
            }
            else
            {
              result.Enqueue(GetToken(characters, tokenStopCharacters));
            }
            break;
        }
      }
      return result;
    }

    private void If()
    {
      object condition = stack.Pop();
      object body = stack.Pop();
      Evaluate(condition);
      if ((dynamic)stack.Pop())
      {
        Evaluate(body);
      }
    }

    private void Invoke()
    {
      BindingFlags memberKind = (BindingFlags)stack.Pop();
      BindingFlags memberType = (BindingFlags)stack.Pop();
      string memberName = (string)stack.Pop();
      string typeName = (string)stack.Pop();
      string assemblyName = (string)stack.Pop();
      int argumentsCount = (int)stack.Pop();
      object[] arguments = stack.Pop(argumentsCount);
      Assembly assembly = Assembly.LoadWithPartialName(assemblyName);
      Type type = assembly.GetType(typeName);
      BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | memberKind | memberType;
      bool isInstance = memberType.HasFlag(BindingFlags.Instance);
      bool isConstructor = memberKind.HasFlag(BindingFlags.CreateInstance);
      object target = isInstance && !isConstructor ? stack.Pop() : null;
      object result = type.InvokeMember(memberName, bindingFlags, null, target, arguments);
      stack.Push(result);
    }

    private void Length()
    {
      stack.Push(((Items)stack.Pop()).Count());
    }

    private void Null()
    {
      stack.Push(null);
    }

    private void Process(object unit)
    {
      if (words.TryGetValue(unit.ToString(), out object word))
      {
        if (word is Action action)
        {
          action.Invoke();
          return;
        }
        Evaluate(word);
        return;
      }
      stack.Push(unit);
    }

    private void LeftAngle()
    {
    }

    private void LeftBrace()
    {
    }

    private void LeftBracket()
    {
    }

    private void LeftParenthesis()
    {
    }

    private void PostLeftAngle()
    {
      words.EnterScope();
    }

    private void PostLeftParenthesis()
    {
    }

    private void PreRightAngle()
    {
      words.LeaveScope();
    }

    private void PreRightParenthesis()
    {
    }

    private void RightAngle()
    {
    }

    private void RightBrace()
    {
    }

    private void RightBracket()
    {
    }

    private void RightParenthesis()
    {
    }

    private void Set()
    {
      foreach (object currentItem in Enumerable.Reverse((Items)stack.Pop()))
      {
        words[currentItem.ToString()] = stack.Pop();
      }
    }

    private void Split()
    {
      object items = stack.Pop();
      int stackLength = stack.Count;
      Evaluate(items);
      stack.Push(stack.Count - stackLength);
    }

    private void While()
    {
      object condition = stack.Pop();
      object body = stack.Pop();
      Evaluate(condition);
      while ((dynamic)stack.Pop())
      {
        Evaluate(body);
        Evaluate(condition);
      }
    }
  }
}