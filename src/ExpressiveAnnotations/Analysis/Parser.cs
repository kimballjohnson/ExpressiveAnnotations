﻿/* https://github.com/jwaliszko/ExpressiveAnnotations
 * Copyright (c) 2014 Jarosław Waliszko
 * Licensed MIT: http://opensource.org/licenses/MIT */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ExpressiveAnnotations.Analysis
{
    /* EBNF GRAMMAR:
     * 
     * expression  => cond-exp
     * cond-exp    => l-or-exp [ "?" cond-exp ":" cond-exp ]
     * l-or-exp    => l-and-exp [ "||" l-or-exp ]
     * l-and-exp   => b-or-exp [ "&&" l-and-exp ]
     * b-or-exp    => xor-exp [ "|" b-or-exp ]
     * xor-exp     => b-and-exp [ "^" xor-exp ]
     * b-and-exp   => eq-exp [ "&" b-and-exp ]     
     * eq-exp      => rel-exp [ "==" | "!=" eq-exp ]
     * rel-exp     => shift-exp [ ">" | ">=" | "<" | "<=" rel-exp ]
     * shift-exp   => add-exp [ "<<" | ">>" shift-exp ]
     * add-exp     => mul-exp add-exp'
     * add-exp'    => "+" | "-" add-exp
     * mul-exp     => unary-exp mul-exp'
     * mul-exp'    => "*" | "/" | "%" mul-exp
     * unary-exp   => primary-exp [ "+" | "-" | "!" | "~" unary-exp ]
     * primary-exp => "null" | "true" | "false" | int | float | bin | hex | string | 
     *                func-call | subscrit | mem-access | "(" cond-exp ")"
     * func-call   => id "(" cond-exp [ "," cond-exp ] ")"
     * subscript   => id "[" int "]"
     * mem-access  => id [ "." id ]
     */

    /// <summary>
    ///     Performs the syntactic analysis of a specified logical expression within given context.
    /// </summary>
    /// <remarks>
    ///     Type is thread safe.
    /// </remarks>
    public sealed class Parser
    {
        private readonly object _locker = new object();

        /// <summary>
        ///     Initializes a new instance of the <see cref="Parser" /> class.
        /// </summary>
        public Parser()
        {
            Fields = new Dictionary<string, Type>();
            Consts = new Dictionary<string, object>();
            Functions = new Dictionary<string, IList<LambdaExpression>>();
        }

        private Stack<Token> TokensToProcess { get; set; }
        private Stack<Token> TokensProcessed { get; set; }
        private string Expr { get; set; }
        private TypeWall Wall { get; set; }
        private Type ContextType { get; set; }
        private Expression ContextExpression { get; set; }
        private IDictionary<string, Type> Fields { get; set; }
        private IDictionary<string, object> Consts { get; set; }
        private IDictionary<string, IList<LambdaExpression>> Functions { get; set; }

        /// <summary>
        ///     Parses a specified logical expression into expression tree within given context.
        /// </summary>
        /// <typeparam name="TContext">The type identifier of the context within which the expression is interpreted.</typeparam>
        /// <param name="expression">The logical expression.</param>
        /// <returns>
        ///     A delegate containing the compiled version of the lambda expression described by created expression tree.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">expression;Expression not provided.</exception>
        /// <exception cref="ParseErrorException"></exception>
        public Func<TContext, bool> Parse<TContext>(string expression)
        {
            if (expression == null)
                throw new ArgumentNullException(nameof(expression), "Expression not provided.");

            lock (_locker)
            {
                try
                {
                    Clear();
                    ContextType = typeof (TContext);
                    var param = Expression.Parameter(typeof (TContext));
                    ContextExpression = param;
                    Expr = expression;
                    Wall = new TypeWall(expression);
                    var expressionTree = ParseExpression();
                    var lambda = Expression.Lambda<Func<TContext, bool>>(expressionTree, param);
                    return lambda.Compile();
                }
                catch (ParseErrorException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new ParseErrorException("Parse fatal error.", e);
                }
            }
        }

        /// <summary>
        ///     Parses a specified logical expression into expression tree within given context.
        /// </summary>
        /// <param name="context">The type instance of the context within which the expression is interpreted.</param>
        /// <param name="expression">The logical expression.</param>
        /// <returns>
        ///     A delegate containing the compiled version of the lambda expression described by created expression tree.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">expression;Expression not provided.</exception>
        /// <exception cref="ParseErrorException"></exception>
        public Func<object, bool> Parse(Type context, string expression)
        {
            if (expression == null)
                throw new ArgumentNullException(nameof(expression), "Expression not provided.");

            lock (_locker)
            {
                try
                {
                    Clear();
                    ContextType = context;
                    var param = Expression.Parameter(typeof (object));
                    ContextExpression = Expression.Convert(param, context);
                    Expr = expression;
                    Wall = new TypeWall(expression);
                    var expressionTree = ParseExpression();
                    var lambda = Expression.Lambda<Func<object, bool>>(expressionTree, param);
                    return lambda.Compile();
                }
                catch (ParseErrorException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    throw new ParseErrorException("Parse fatal error.", e);
                }
            }
        }

        /// <summary>
        ///     Registers function signature for the parser.
        /// </summary>
        /// <typeparam name="TResult">Type identifier of returned result.</typeparam>
        /// <param name="name">Function name.</param>
        /// <param name="func">Function lambda.</param>
        public void AddFunction<TResult>(string name, Expression<Func<TResult>> func)
        {
            PersistFunction(name, func);
        }

        /// <summary>
        ///     Registers function signature for the parser.
        /// </summary>
        /// <typeparam name="TArg1">First argument.</typeparam>
        /// <typeparam name="TResult">Type identifier of returned result.</typeparam>
        /// <param name="name">Function name.</param>
        /// <param name="func">Function lambda.</param>
        public void AddFunction<TArg1, TResult>(string name, Expression<Func<TArg1, TResult>> func)
        {
            PersistFunction(name, func);
        }

        /// <summary>
        ///     Registers function signature for the parser.
        /// </summary>
        /// <typeparam name="TArg1">First argument.</typeparam>
        /// <typeparam name="TArg2">Second argument.</typeparam>
        /// <typeparam name="TResult">Type identifier of returned result.</typeparam>
        /// <param name="name">Function name.</param>
        /// <param name="func">Function lambda.</param>
        public void AddFunction<TArg1, TArg2, TResult>(string name, Expression<Func<TArg1, TArg2, TResult>> func)
        {
            PersistFunction(name, func);
        }

        /// <summary>
        ///     Registers function signature for the parser.
        /// </summary>
        /// <typeparam name="TArg1">First argument.</typeparam>
        /// <typeparam name="TArg2">Second argument.</typeparam>
        /// <typeparam name="TArg3">Third argument.</typeparam>
        /// <typeparam name="TResult">Type identifier of returned result.</typeparam>
        /// <param name="name">Function name.</param>
        /// <param name="func">Function lambda.</param>
        public void AddFunction<TArg1, TArg2, TArg3, TResult>(string name, Expression<Func<TArg1, TArg2, TArg3, TResult>> func)
        {
            PersistFunction(name, func);
        }

        /// <summary>
        ///     Registers function signature for the parser.
        /// </summary>
        /// <typeparam name="TArg1">First argument.</typeparam>
        /// <typeparam name="TArg2">Second argument.</typeparam>
        /// <typeparam name="TArg3">Third argument.</typeparam>
        /// <typeparam name="TArg4">Fourth argument.</typeparam>
        /// <typeparam name="TResult">Type identifier of returned result.</typeparam>
        /// <param name="name">Function name.</param>
        /// <param name="func">Function lambda.</param>
        public void AddFunction<TArg1, TArg2, TArg3, TArg4, TResult>(string name, Expression<Func<TArg1, TArg2, TArg3, TArg4, TResult>> func)
        {
            PersistFunction(name, func);
        }

        /// <summary>
        ///     Registers function signature for the parser.
        /// </summary>
        /// <typeparam name="TArg1">First argument.</typeparam>
        /// <typeparam name="TArg2">Second argument.</typeparam>
        /// <typeparam name="TArg3">Third argument.</typeparam>
        /// <typeparam name="TArg4">Fourth argument.</typeparam>
        /// <typeparam name="TArg5">Fifth argument.</typeparam>
        /// <typeparam name="TResult">Type identifier of returned result.</typeparam>
        /// <param name="name">Function name.</param>
        /// <param name="func">Function lambda.</param>
        public void AddFunction<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(string name, Expression<Func<TArg1, TArg2, TArg3, TArg4, TArg5, TResult>> func)
        {
            PersistFunction(name, func);
        }

        /// <summary>
        ///     Registers function signature for the parser.
        /// </summary>
        /// <typeparam name="TArg1">First argument.</typeparam>
        /// <typeparam name="TArg2">Second argument.</typeparam>
        /// <typeparam name="TArg3">Third argument.</typeparam>
        /// <typeparam name="TArg4">Fourth argument.</typeparam>
        /// <typeparam name="TArg5">Fifth argument.</typeparam>
        /// <typeparam name="TArg6">Sixth argument.</typeparam>
        /// <typeparam name="TResult">Type identifier of returned result.</typeparam>
        /// <param name="name">Function name.</param>
        /// <param name="func">Function lambda.</param>
        public void AddFunction<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TResult>(string name, Expression<Func<TArg1, TArg2, TArg3, TArg4, TArg5, TArg6, TResult>> func)
        {
            PersistFunction(name, func);
        }

        /// <summary>
        ///     Gets names and types of properties extracted from specified expression within given context.
        /// </summary>
        /// <returns>
        ///     Dictionary containing names and types.
        /// </returns>
        public IDictionary<string, Type> GetFields()
        {
            return Fields.ToDictionary(x => x.Key, x => x.Value);
        }

        /// <summary>
        ///     Gets names and values of constants extracted from specified expression within given context.
        /// </summary>
        /// <returns>
        ///     Dictionary containing names and values.
        /// </returns>
        public IDictionary<string, object> GetConsts()
        {
            return Consts.ToDictionary(x => x.Key, x => x.Value); // shallow clone is fair enough
        }

        private void PersistFunction(string name, LambdaExpression func)
        {
            lock (_locker)
            {
                if (!Functions.ContainsKey(name))
                    Functions[name] = new List<LambdaExpression>();
                Functions[name].Add(func);
            }
        }

        private void Clear()
        {
            Fields.Clear();
            Consts.Clear();
        }

        private void Tokenize()
        {
            var lexer = new Lexer();
            var tokens = lexer.Analyze(Expr);
            TokensToProcess = new Stack<Token>(tokens.Reverse());
            TokensProcessed = new Stack<Token>();
        }

        private TokenType PeekType()
        {
            return TokensToProcess.Peek().Type;
        }

        private object PeekValue()
        {
            return TokensToProcess.Peek().Value;
        }

        private string PeekRawValue() // to be displayed in error messages, e.g. instead of converted 0.1 gets raw .1
        {
            return TokensToProcess.Peek().RawValue;
        }

        private void ReadToken()
        {
            TokensProcessed.Push(TokensToProcess.Pop());
        }

        private Token PeekToken(int depth = 0)
        {
            Debug.Assert(depth >= 0); // depth can not be negative

            return depth == 0
                ? TokensToProcess.Peek() // for 0 depth take crrent context
                : TokensProcessed.Skip(depth - 1).First(); // otherwise dig through processed tokens
        }

        private Expression ParseExpression()
        {
            Tokenize();
            var expr = ParseConditionalExpression();
            if (PeekType() != TokenType.EOF)
                throw new ParseErrorException(
                    $"Unexpected token: '{PeekRawValue()}'.", Expr, PeekToken().Location);
            return expr;
        }

        private Expression ParseConditionalExpression()
        {
            var arg1 = ParseLogicalOrExp();
            if (PeekType() != TokenType.QMARK)
                return arg1;
            ReadToken();
            var arg2 = ParseConditionalExpression();
            if (PeekType() != TokenType.COLON)
                throw new ParseErrorException(
                    PeekType() == TokenType.EOF
                        ? "Expected colon of ternary operator. Unexpected end of expression."
                        : $"Expected colon of ternary operator. Unexpected token: '{PeekRawValue()}'.",
                    Expr, PeekToken().Location);
            ReadToken();
            var arg3 = ParseConditionalExpression();

            return Expression.Condition(arg1, arg2, arg3);
        }

        private Expression ParseLogicalOrExp()
        {
            var arg1 = ParseLogicalAndExp();
            if (PeekType() != TokenType.L_OR)
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseLogicalOrExp();

            Wall.LOr(arg1, arg2, oper);

            return Expression.OrElse(arg1, arg2); // short-circuit evaluation
        }

        private Expression ParseLogicalAndExp()
        {
            var arg1 = ParseBitwiseOrExp();
            if (PeekType() != TokenType.L_AND)
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseLogicalAndExp();

            Wall.LAnd(arg1, arg2, oper);

            return Expression.AndAlso(arg1, arg2); // short-circuit evaluation
        }

        private Expression ParseBitwiseOrExp()
        {
            var arg1 = ParseXorExp();
            if (PeekType() != TokenType.B_OR)
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseBitwiseOrExp();

            Wall.BOr(arg1, arg2, oper);

            return Expression.Or(arg1, arg2); // non-short-circuit evaluation
        }

        private Expression ParseXorExp()
        {
            var arg1 = ParseBitwiseAndExp();
            if (PeekType() != TokenType.XOR)
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseXorExp();

            Wall.Xor(arg1, arg2, oper);

            return Expression.ExclusiveOr(arg1, arg2);
        }

        private Expression ParseBitwiseAndExp()
        {
            var arg1 = ParseEqualityExp();
            if (PeekType() != TokenType.B_AND)
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseBitwiseAndExp();

            Wall.BAnd(arg1, arg2, oper);

            return Expression.And(arg1, arg2); // non-short-circuit evaluation
        }

        private Expression ParseEqualityExp()
        {
            var arg1 = ParseRelationalExp();
            if (!new[] {TokenType.EQ, TokenType.NEQ}.Contains(PeekType()))
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseEqualityExp();

            var type1 = arg1.Type;
            var type2 = arg2.Type;
            Helper.MakeTypesCompatible(arg1, arg2, out arg1, out arg2);
            Wall.Eq(arg1, arg2, type1, type2, oper);

            switch (oper.Type)
            {
                case TokenType.EQ:
                    return Expression.Equal(arg1, arg2);
                default: // assures full branch coverage
                    Debug.Assert(oper.Type == TokenType.NEQ); // http://stackoverflow.com/a/1468385/270315
                    return Expression.NotEqual(arg1, arg2);
            }
        }

        private Expression ParseRelationalExp()
        {
            var arg1 = ParseShiftExp();
            if (!new[] {TokenType.LT, TokenType.LE, TokenType.GT, TokenType.GE}.Contains(PeekType()))
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseRelationalExp();

            var type1 = arg1.Type;
            var type2 = arg2.Type;
            Helper.MakeTypesCompatible(arg1, arg2, out arg1, out arg2);
            Wall.Rel(arg1, arg2, type1, type2, oper);

            switch (oper.Type)
            {
                case TokenType.LT:
                    return Expression.LessThan(arg1, arg2);
                case TokenType.LE:
                    return Expression.LessThanOrEqual(arg1, arg2);
                case TokenType.GT:
                    return Expression.GreaterThan(arg1, arg2);
                default:
                    Debug.Assert(oper.Type == TokenType.GE);
                    return Expression.GreaterThanOrEqual(arg1, arg2);
            }
        }

        private Expression ParseShiftExp()
        {
            var arg1 = ParseAdditiveExp();
            if (!new[] {TokenType.L_SHIFT, TokenType.R_SHIFT}.Contains(PeekType()))
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseShiftExp();

            Wall.Shift(arg1, arg2, oper);

            switch (oper.Type)
            {
                case TokenType.L_SHIFT:
                    return Expression.LeftShift(arg1, arg2);
                default:
                    Debug.Assert(oper.Type == TokenType.R_SHIFT);
                    return Expression.RightShift(arg1, arg2);
            }
        }

        private Expression ParseAdditiveExp()
        {
            var arg = ParseMultiplicativeExp();
            return ParseAdditiveExpInternal(arg);
        }

        private Expression ParseAdditiveExpInternal(Expression arg1)
        {
            if (!new[] {TokenType.ADD, TokenType.SUB}.Contains(PeekType()))
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseMultiplicativeExp();

            var type1 = arg1.Type;
            var type2 = arg2.Type;
            Helper.MakeTypesCompatible(arg1, arg2, out arg1, out arg2);
            Wall.Add(arg1, arg2, type1, type2, oper);

            switch (oper.Type)
            {
                case TokenType.ADD:
                    return ParseAdditiveExpInternal(
                        (arg1.Type.IsString() || arg2.Type.IsString())
                            ? Expression.Add(
                                Expression.Convert(arg1, typeof (object)),
                                Expression.Convert(arg2, typeof (object)),
                                typeof (string).GetMethod("Concat", new[] {typeof (object), typeof (object)})) // convert string + string into a call to string.Concat
                            : Expression.Add(arg1, arg2));
                default:
                    Debug.Assert(oper.Type == TokenType.SUB);
                    return ParseAdditiveExpInternal(Expression.Subtract(arg1, arg2));
            }
        }

        private Expression ParseMultiplicativeExp()
        {
            var arg = ParseUnaryExp();
            return ParseMultiplicativeExpInternal(arg);
        }

        private Expression ParseMultiplicativeExpInternal(Expression arg1)
        {
            if (!new[] {TokenType.MUL, TokenType.DIV, TokenType.MOD}.Contains(PeekType()))
                return arg1;
            var oper = PeekToken();
            ReadToken();
            var arg2 = ParseUnaryExp();

            Wall.Mul(arg1, arg2, oper);
            Helper.MakeTypesCompatible(arg1, arg2, out arg1, out arg2);

            switch (oper.Type)
            {
                case TokenType.MUL:
                    return ParseMultiplicativeExpInternal(Expression.Multiply(arg1, arg2));
                case TokenType.DIV:
                    return ParseMultiplicativeExpInternal(Expression.Divide(arg1, arg2));
                default:
                    Debug.Assert(oper.Type == TokenType.MOD);
                    return ParseMultiplicativeExpInternal(Expression.Modulo(arg1, arg2));
            }
        }

        private Expression ParseUnaryExp()
        {
            if (!new[] {TokenType.ADD, TokenType.SUB, TokenType.L_NOT, TokenType.B_NOT}.Contains(PeekType()))
                return ParsePrimaryExp();
            var oper = PeekToken();
            ReadToken();
            var arg = ParseUnaryExp(); // allow multiple negations

            Wall.Unary(arg, oper);

            switch (oper.Type)
            {
                case TokenType.ADD:
                    return arg;
                case TokenType.SUB:
                    return Expression.Negate(arg);
                case TokenType.L_NOT:
                    return Expression.Not(arg);
                default:
                    Debug.Assert(oper.Type == TokenType.B_NOT);
                    return Expression.OnesComplement(arg);
            }
        }

        private Expression ParsePrimaryExp()
        {
            switch (PeekType())
            {
                case TokenType.NULL:
                    return ParseNull();
                case TokenType.INT:
                case TokenType.BIN:
                case TokenType.HEX:
                    return ParseInt();
                case TokenType.FLOAT:
                    return ParseFloat();
                case TokenType.BOOL:
                    return ParseBool();
                case TokenType.STRING:
                    return ParseString();
                case TokenType.ID:
                    return ParseId();
                case TokenType.L_PAR:
                    ReadToken(); // read "("
                    var arg = ParseConditionalExpression();
                    if (PeekType() != TokenType.R_PAR)
                        throw new ParseErrorException(
                            PeekType() == TokenType.EOF
                                ? "Expected closing bracket. Unexpected end of expression."
                                : $"Expected closing bracket. Unexpected token: '{PeekRawValue()}'.",
                            Expr, PeekToken().Location);
                    ReadToken(); // read ")"
                    return arg;
                case TokenType.EOF:
                    throw new ParseErrorException(
                        "Expected \"null\", int, float, bool, bin, hex, string or id. Unexpected end of expression.", Expr, PeekToken().Location);                
                default:
                    throw new ParseErrorException(
                        $"Expected \"null\", int, float, bool, bin, hex, string or id. Unexpected token: '{PeekRawValue()}'.", Expr, PeekToken().Location);
            }
        }

        private Expression ParseNull()
        {
            ReadToken();
            return Expression.Constant(null);
        }

        private Expression ParseInt()
        {
            var value = PeekValue();
            ReadToken();
            return Expression.Constant(value, typeof (int));
        }

        private Expression ParseFloat()
        {
            var value = PeekValue();
            ReadToken();
            return Expression.Constant(value, typeof (double));
        }

        private Expression ParseBool()
        {
            var value = PeekValue();
            ReadToken();
            return Expression.Constant(value, typeof (bool));
        }

        private Expression ParseString()
        {
            var value = PeekValue();
            ReadToken();
            return Expression.Constant(value, typeof (string));
        }

        private Expression ParseId()
        {
            var func = PeekToken();
            ReadToken(); // read name

            switch (PeekType())
            {
                case TokenType.L_PAR:
                    return ParseFuncCall(func);
                default:
                    return ParseMemberAccess(func);
            }
        }

        private Expression ParseFuncCall(Token func)
        {
            var name = func.RawValue;
            ReadToken(); // read "("
            var args = new List<Tuple<Expression, Location>>();
            while (PeekType() != TokenType.R_PAR) // read comma-separated arguments until we hit ")"
            {
                var tkn = PeekToken();
                var arg = ParseConditionalExpression();
                if (PeekType() == TokenType.COMMA)
                    ReadToken();
                else if (PeekType() != TokenType.R_PAR) // when no comma found, function exit expected
                    throw new ParseErrorException(
                        PeekType() == TokenType.EOF
                            ? $"Function '{name}' expects comma or closing bracket. Unexpected end of expression."
                            : $"Function '{name}' expects comma or closing bracket. Unexpected token: '{PeekRawValue()}'.",
                        Expr, PeekToken().Location);
                args.Add(new Tuple<Expression, Location>(arg, tkn.Location));
            }
            ReadToken(); // read ")"

            return ExtractMethodExpression(name, args, func.Location); // get method call
        }

        private Expression ParseMemberAccess(Token prop)
        {
            var name = prop.RawValue;
            var builder = new StringBuilder(name);
            while (new[] {TokenType.L_BRACKET, TokenType.PERIOD}.Contains(PeekType()))
            {
                switch (PeekType())
                {
                    case TokenType.PERIOD: // parse member access
                        builder.Append(PeekValue());
                        ReadToken(); // read "."
                        if(PeekType() != TokenType.ID)
                            throw new ParseErrorException(
                                PeekType() == TokenType.EOF
                                    ? $"Member '{name}' expects subproperty identifier. Unexpected end of expression."
                                    : $"Member '{name}' expects subproperty identifier. Unexpected token: '{PeekRawValue()}'.",
                                Expr, PeekToken().Location);
                        name = PeekRawValue();
                        builder.Append(name);
                        ReadToken();
                        break;
                    default: // parse subscrit
                        Debug.Assert(PeekType() == TokenType.L_BRACKET);
                        builder.Append(PeekValue());
                        ReadToken(); // read "["
                        if (PeekType() != TokenType.INT)
                            throw new ParseErrorException(
                                PeekType() == TokenType.EOF
                                    ? $"Array '{name}' expects integral index. Unexpected end of expression."
                                    : $"Array '{name}' expects integral index. Unexpected token: '{PeekRawValue()}'.",
                                Expr, PeekToken().Location);
                        builder.Append(PeekValue());
                        ReadToken();
                        if (PeekType() != TokenType.R_BRACKET)
                            throw new ParseErrorException(
                                PeekType() == TokenType.EOF
                                    ? $"Array '{name}' expects closing bracket. Unexpected end of expression."
                                    : $"Array '{name}' expects closing bracket. Unexpected token: '{PeekRawValue()}'.",
                                Expr, PeekToken().Location);
                        builder.Append(PeekValue());
                        ReadToken(); // read "]"
                        break;
                }
            }

            return ExtractMemberExpression(builder.ToString(), prop.Location);
        }

        private Expression ExtractMemberExpression(string name, Location pos)
        {
            var expression = FetchPropertyValue(name, pos) ?? FetchEnumValue(name, pos) ?? FetchConstValue(name, pos);
            if (expression == null)
                throw new ParseErrorException(
                    $"Only public properties, constants and enums are accepted. Identifier '{name}' not known.", Expr, pos);

            return expression;
        }

        private Expression FetchPropertyValue(string name, Location pos)
        {
            var type = ContextType;
            var expr = ContextExpression;
            var parts = name.Split('.');

            var regex = new Regex(@"([a-zA-z_0-9]+)\[([0-9]+)\]"); // regex matching array element access

            foreach (var part in parts)
            {
                PropertyInfo pi;

                var match = regex.Match(part);                
                if (match.Success)
                {
                    var partName = match.Groups[1].Value;                    
                    var idx = int.Parse(match.Groups[2].Value);

                    pi = type.GetProperty(partName);
                    if (pi == null)
                        return null;

                    var property = Expression.Property(expr, pi);
                    if (pi.PropertyType.IsArray) // check if we have an array type
                    {
                        expr = Expression.ArrayIndex(property, Expression.Constant(idx));
                        type = pi.PropertyType.GetElementType();
                        continue;
                    }

                    // not an array - check if the type declares indexer otherwise
                    pi = pi.PropertyType.GetProperties().FirstOrDefault(p => p.GetIndexParameters().Any()); // look for indexer property (usually called Item...)
                    if (pi != null)
                    {
                        expr = Expression.Property(property, pi.Name, Expression.Constant(idx));
                        type = pi.PropertyType;
                        continue;
                    }

                    throw new ParseErrorException(
                        $"Identifier '{name.Substring(0, name.Length - 2 - idx.ToString().Length)}' either does not represent an array type or does not declare indexer.",
                        Expr, pos);
                }

                pi = type.GetProperty(part);
                if (pi == null)
                    return null;

                expr = Expression.Property(expr, pi);
                type = pi.PropertyType;
            }

            Fields[name] = type;
            return expr;
        }

        private Expression FetchEnumValue(string name, Location pos)
        {
            var parts = name.Split('.');
            if (parts.Length > 1)
            {
                var enumTypeName = string.Join(".", parts.Take(parts.Length - 1).ToList());
                var enumTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => new AssemblyTypeProvider(a).GetLoadableTypes())
                    .Where(t => t.IsEnum && string.Concat(".", t.FullName.Replace("+", ".")).EndsWith(string.Concat(".", enumTypeName)))
                    .ToList();

                if (enumTypes.Count > 1)
                    throw new ParseErrorException(
                        $"Enum '{enumTypeName}' is ambiguous, found following:{Environment.NewLine}{string.Join("," + Environment.NewLine, enumTypes.Select(x => $"'{x.FullName}'"))}.",
                        Expr, pos);

                var type = enumTypes.SingleOrDefault();
                if (type != null)
                {
                    var value = Enum.Parse(type, parts.Last());
                    Consts[name] = value;
                    return Expression.Constant(value);
                }
            }
            return null;
        }

        private Expression FetchConstValue(string name, Location pos)
        {
            FieldInfo constant;
            var parts = name.Split('.');
            if (parts.Length > 1)
            {
                var constTypeName = string.Join(".", parts.Take(parts.Length - 1).ToList());
                var constants = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => new AssemblyTypeProvider(a).GetLoadableTypes())
                    .Where(t => string.Concat(".", t.FullName.Replace("+", ".")).EndsWith(string.Concat(".", constTypeName)))
                    .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                        .Where(fi => fi.IsLiteral && !fi.IsInitOnly && fi.Name.Equals(parts.Last())))
                    .ToList();

                if (constants.Count > 1)
                    throw new ParseErrorException(
                        $"Constant '{name}' is ambiguous, found following:{Environment.NewLine}{string.Join("," + Environment.NewLine, constants.Select(x => x.ReflectedType != null ? $"'{x.ReflectedType.FullName}.{x.Name}'" : $"'{x.Name}'"))}.",
                        Expr, pos);

                constant = constants.SingleOrDefault();
                
            }
            else
            {
                constant = ContextType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    .SingleOrDefault(fi => fi.IsLiteral && !fi.IsInitOnly && fi.Name.Equals(name));
            }

            if (constant == null) 
                return null;

            var value = constant.GetRawConstantValue();
            Consts[name] = (value as string)?.Replace(Environment.NewLine, "\n") ?? value; // in our language new line is represented by \n char (consts map is then sent to JavaScript, and JavaScript new line is also \n)
            return Expression.Constant(value);
        }

        private Expression ExtractMethodExpression(string name, IList<Tuple<Expression, Location>> args, Location funcPos)
        {
            AssertMethodNameExistence(name, funcPos);
            var expression = FetchModelMethod(name, args, funcPos) ?? FetchToolchainMethod(name, args, funcPos); // firstly, try to take method from model context - if not found, take one from toolchain
            if (expression == null)
                throw new ParseErrorException(
                    $"Function '{name}' accepting {args.Count} argument{(args.Count == 1 ? string.Empty : "s")} not found.", Expr, funcPos);

            return expression;
        }

        private Expression FetchModelMethod(string name, IList<Tuple<Expression, Location>> args, Location funcPos)
        {
            var signatures = ContextType.GetMethods()
                .Where(mi => name.Equals(mi.Name) && mi.GetParameters().Length == args.Count).ToList();
            if (signatures.Count == 0)
                return null;
            AssertNonAmbiguity(signatures.Count, name, args.Count, funcPos);

            return CreateMethodCallExpression(ContextExpression, args, signatures.Single());
        }

        private Expression FetchToolchainMethod(string name, IList<Tuple<Expression, Location>> args, Location funcPos)
        {
            var signatures = Functions.ContainsKey(name)
                ? Functions[name].Where(f => f.Parameters.Count == args.Count).ToList()
                : new List<LambdaExpression>();
            if (signatures.Count == 0)
                return null;
            AssertNonAmbiguity(signatures.Count, name, args.Count, funcPos);

            return CreateInvocationExpression(signatures.Single(), args, name);
        }

        private InvocationExpression CreateInvocationExpression(LambdaExpression funcExpr, IList<Tuple<Expression, Location>> parsedArgs, string funcName)
        {
            Debug.Assert(funcExpr.Parameters.Count == parsedArgs.Count);
            var convertedArgs = new List<Expression>();
            for (var i = 0; i < parsedArgs.Count; i++)
            {
                var arg = parsedArgs[i].Item1;
                var pos = parsedArgs[i].Item2;
                var param = funcExpr.Parameters[i];
                convertedArgs.Add(arg.Type == param.Type
                    ? arg
                    : ConvertArgument(arg, param.Type, funcName, i + 1, pos));
            }
            return Expression.Invoke(funcExpr, convertedArgs);
        }

        private MethodCallExpression CreateMethodCallExpression(Expression contextExpression, IList<Tuple<Expression, Location>> parsedArgs, MethodInfo methodInfo)
        {
            var parameters = methodInfo.GetParameters();
            Debug.Assert(parameters.Length == parsedArgs.Count);
            var convertedArgs = new List<Expression>();
            for (var i = 0; i < parsedArgs.Count; i++)
            {
                var arg = parsedArgs[i].Item1;
                var pos = parsedArgs[i].Item2;
                var param = parameters[i];
                convertedArgs.Add(arg.Type == param.ParameterType
                    ? arg
                    : ConvertArgument(arg, param.ParameterType, methodInfo.Name, i + 1, pos));
            }
            return Expression.Call(contextExpression, methodInfo, convertedArgs);
        }        

        private Expression ConvertArgument(Expression arg, Type type, string funcName, int argIdx, Location argPos)
        {
            try
            {
                return Expression.Convert(arg, type);
            }
            catch
            {
                throw new ParseErrorException(
                    $"Function '{funcName}' {argIdx.ToOrdinal()} argument implicit conversion from '{arg.Type}' to expected '{type}' failed.",
                    Expr, argPos);
            }
        }

        private void AssertMethodNameExistence(string name, Location funcPos)
        {
            if (!Functions.ContainsKey(name) && !ContextType.GetMethods().Any(mi => name.Equals(mi.Name)))
                throw new ParseErrorException(
                    $"Function '{name}' not known.", Expr, funcPos);
        }

        private void AssertNonAmbiguity(int signatures, string funcName, int args, Location funcPos)
        {
            if (signatures > 1)
                throw new ParseErrorException(
                    $"Function '{funcName}' accepting {args} argument{(args == 1 ? string.Empty : "s")} is ambiguous.", Expr, funcPos);
        }
    }
}
