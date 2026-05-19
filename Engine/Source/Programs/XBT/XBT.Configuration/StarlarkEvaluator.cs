// Copyright Simgenics. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Simgenics.XPact.XBT.Manifest;

namespace Simgenics.XPact.XBT.Configuration;

/// <summary>
/// Sandboxed evaluator for the Bazel-Starlark-0.10.0-derived expression
/// subset that XPact uses for <c>.Build.expr</c> conditionals. Per
/// Toolchain Contract Rev 13 Section 9.6 and
/// <c>/Documents/XBT.html</c> Rev 4 Section 3.3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Grammar (BNF; matches Contract Section 9.6).</b>
/// </para>
/// <code>
/// expr        ::= conditional
/// conditional ::= or_expr ('if' or_expr 'else' conditional)?
/// or_expr     ::= and_expr ('or' and_expr)*
/// and_expr    ::= not_expr ('and' not_expr)*
/// not_expr    ::= 'not' not_expr | comparison
/// comparison  ::= unary (comp_op unary)*
/// comp_op     ::= '==' | '!=' | '&lt;' | '&gt;' | '&lt;=' | '&gt;=' | 'in' | 'not' 'in'
/// unary       ::= ('+' | '-') unary | primary
/// primary     ::= call | atom
/// call        ::= atom ('.' identifier '(' arglist? ')')*
/// arglist     ::= expr (',' expr)*
/// atom        ::= literal | identifier ('.' identifier)* | '(' expr ')' | list
/// literal     ::= STRING | INT | BOOL | 'None'
/// list        ::= '[' (expr (',' expr)*)? ']'
/// </code>
/// <para>
/// <b>Sandbox guarantees</b> (Contract Section 9.6 deviations):
/// </para>
/// <list type="bullet">
///   <item>No file I/O. <c>load</c>, <c>print</c>, and any file-reading
///   identifier is unbound (and would fail at evaluation as an
///   unknown identifier).</item>
///   <item>No mutation. The evaluator is a pure function of
///   (input string, bindings).</item>
///   <item>No <c>def</c>. Function definitions are not part of the
///   accepted grammar.</item>
///   <item>No <c>for</c> loops. Loops are not part of the grammar.</item>
///   <item>No list / dict comprehensions. Omitted per Contract for
///   budget predictability.</item>
///   <item>Evaluation budget: 100,000 instructions per top-level
///   expression. Exceeding fails with a
///   <see cref="DescriptorParseException"/> carrying exit code 30.</item>
/// </list>
/// </remarks>
public static class StarlarkEvaluator
{
    /// <summary>
    /// Maximum number of "instructions" any single expression may
    /// consume. Each AST-node evaluation counts as one instruction;
    /// short-circuit branches that skip work still cost one for the
    /// short-circuit decision itself. Per Contract Section 9.6
    /// deviation (f).
    /// </summary>
    public const int InstructionBudget = 100_000;

    /// <summary>
    /// Bindings table for the expression sub-language. Built by the
    /// caller (typically <c>XBT.Discovery</c>) from the active target
    /// + module per Contract Section 9.6.
    /// </summary>
    public sealed class Bindings
    {
        /// <summary><c>target.platform</c>: "Win64" / "Linux" / "Android".</summary>
        public required string TargetPlatform { get; init; }

        /// <summary><c>target.configuration</c>: "Debug" / "DebugGame" / "Development" / "Test" / "Shipping".</summary>
        public required string TargetConfiguration { get; init; }

        /// <summary><c>target.station_role</c>: "Engineer" / "Instructor" / "Trainee" (or "None").</summary>
        public required string TargetStationRole { get; init; }

        /// <summary><c>target.fips_mode</c>: boolean.</summary>
        public required bool TargetFipsMode { get; init; }

        /// <summary><c>target.architecture</c>: "x86_64" / "aarch64".</summary>
        public required string TargetArchitecture { get; init; }

        /// <summary><c>module.tier</c>: "Engine" / "Studio" / "Project".</summary>
        public required string ModuleTier { get; init; }

        /// <summary><c>module.sim_path</c>: boolean.</summary>
        public required bool ModuleSimPath { get; init; }

        /// <summary><c>module.simd_level</c>: "None" / "Default" / "SSE2" / "SSE42" / "AVX" / "AVX2" / "AVX512".</summary>
        public required string ModuleSimdLevel { get; init; }

        /// <summary>
        /// Construct a bindings instance from <see cref="TargetRules"/>
        /// + <see cref="ModuleRules"/>. The most common call site.
        /// </summary>
        public static Bindings From(TargetRules target, ModuleRules module)
        {
            ArgumentNullException.ThrowIfNull(target);
            ArgumentNullException.ThrowIfNull(module);
            return new Bindings
            {
                TargetPlatform = target.Platform.ToString(),
                TargetConfiguration = target.Configuration.ToString(),
                TargetStationRole = target.StationRole.ToString(),
                TargetFipsMode = target.FipsMode,
                TargetArchitecture = target.Architecture,
                ModuleTier = module.Tier.ToString(),
                ModuleSimPath = module.SimPath,
                ModuleSimdLevel = module.SimdLevel.ToString(),
            };
        }
    }

    /// <summary>
    /// Evaluate an expression source string against the supplied
    /// bindings. Returns the result as a boxed object (bool / long /
    /// string / list).
    /// </summary>
    /// <param name="source">The expression source text.</param>
    /// <param name="bindings">Identifier bindings.</param>
    /// <param name="sourcePath">Optional filename for diagnostics.</param>
    /// <exception cref="DescriptorParseException">
    /// On lex / parse failure, unbound identifier, type mismatch in an
    /// operator, or instruction budget exceeded. Exit code 30.
    /// </exception>
    public static object Evaluate(string source, Bindings bindings, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bindings);

        Lexer lexer = new(source, sourcePath);
        List<Token> tokens = lexer.Tokenize();
        Parser parser = new(tokens, sourcePath);
        Node ast = parser.ParseExpression();
        parser.ExpectEof();

        Interpreter interp = new(bindings, sourcePath);
        return interp.Eval(ast);
    }

    // -----------------------------------------------------------------
    // Lexer
    // -----------------------------------------------------------------

    private enum TokenKind
    {
        Identifier,
        String,
        Integer,
        BoolLiteral,
        NoneLiteral,
        LParen,
        RParen,
        LBracket,
        RBracket,
        Comma,
        Dot,
        Plus,
        Minus,
        Eq,
        NEq,
        Lt,
        Gt,
        LEq,
        GEq,
        KwAnd,
        KwOr,
        KwNot,
        KwIf,
        KwElse,
        KwIn,
        Eof,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);

    private sealed class Lexer
    {
        private readonly string _src;
        private readonly string? _sourcePath;
        private int _pos;
        private int _line = 1;
        private int _col = 1;

        public Lexer(string src, string? sourcePath)
        {
            _src = src;
            _sourcePath = sourcePath;
        }

        public List<Token> Tokenize()
        {
            List<Token> result = new();
            while (true)
            {
                SkipWhitespaceAndComments();
                if (_pos >= _src.Length)
                {
                    result.Add(new Token(TokenKind.Eof, "", _line, _col));
                    return result;
                }
                int startLine = _line, startCol = _col;
                char c = _src[_pos];

                if (c == '"' || c == '\'')
                {
                    result.Add(LexString(startLine, startCol));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    result.Add(LexInteger(startLine, startCol));
                    continue;
                }
                if (IsIdentStart(c))
                {
                    result.Add(LexIdentifierOrKeyword(startLine, startCol));
                    continue;
                }

                // Punctuation / operators.
                if (TryMatch("==")) { result.Add(new Token(TokenKind.Eq, "==", startLine, startCol)); continue; }
                if (TryMatch("!=")) { result.Add(new Token(TokenKind.NEq, "!=", startLine, startCol)); continue; }
                if (TryMatch("<=")) { result.Add(new Token(TokenKind.LEq, "<=", startLine, startCol)); continue; }
                if (TryMatch(">=")) { result.Add(new Token(TokenKind.GEq, ">=", startLine, startCol)); continue; }

                switch (c)
                {
                    case '(': Advance(); result.Add(new Token(TokenKind.LParen, "(", startLine, startCol)); continue;
                    case ')': Advance(); result.Add(new Token(TokenKind.RParen, ")", startLine, startCol)); continue;
                    case '[': Advance(); result.Add(new Token(TokenKind.LBracket, "[", startLine, startCol)); continue;
                    case ']': Advance(); result.Add(new Token(TokenKind.RBracket, "]", startLine, startCol)); continue;
                    case ',': Advance(); result.Add(new Token(TokenKind.Comma, ",", startLine, startCol)); continue;
                    case '.': Advance(); result.Add(new Token(TokenKind.Dot, ".", startLine, startCol)); continue;
                    case '+': Advance(); result.Add(new Token(TokenKind.Plus, "+", startLine, startCol)); continue;
                    case '-': Advance(); result.Add(new Token(TokenKind.Minus, "-", startLine, startCol)); continue;
                    case '<': Advance(); result.Add(new Token(TokenKind.Lt, "<", startLine, startCol)); continue;
                    case '>': Advance(); result.Add(new Token(TokenKind.Gt, ">", startLine, startCol)); continue;
                }

                throw new DescriptorParseException(
                    $"Unexpected character '{c}' in .Build.expr.",
                    filePath: _sourcePath,
                    line: startLine,
                    column: startCol);
            }
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < _src.Length)
            {
                char c = _src[_pos];
                if (c == ' ' || c == '\t' || c == '\r')
                {
                    Advance();
                    continue;
                }
                if (c == '\n')
                {
                    _pos++;
                    _line++;
                    _col = 1;
                    continue;
                }
                if (c == '#')
                {
                    while (_pos < _src.Length && _src[_pos] != '\n')
                    {
                        _pos++;
                    }
                    continue;
                }
                return;
            }
        }

        private Token LexString(int startLine, int startCol)
        {
            char quote = _src[_pos];
            Advance(); // opening quote
            StringBuilder sb = new();
            while (_pos < _src.Length && _src[_pos] != quote)
            {
                char c = _src[_pos];
                if (c == '\\')
                {
                    Advance();
                    if (_pos >= _src.Length)
                    {
                        throw new DescriptorParseException(
                            "Unterminated string escape in .Build.expr.",
                            filePath: _sourcePath, line: _line, column: _col);
                    }
                    char esc = _src[_pos];
                    char unescaped = esc switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        '\'' => '\'',
                        _ => throw new DescriptorParseException(
                            $"Unknown string escape '\\{esc}' in .Build.expr.",
                            filePath: _sourcePath, line: _line, column: _col),
                    };
                    sb.Append(unescaped);
                    Advance();
                    continue;
                }
                if (c == '\n')
                {
                    throw new DescriptorParseException(
                        "Newline inside string literal in .Build.expr.",
                        filePath: _sourcePath, line: _line, column: _col);
                }
                sb.Append(c);
                Advance();
            }
            if (_pos >= _src.Length)
            {
                throw new DescriptorParseException(
                    "Unterminated string literal in .Build.expr.",
                    filePath: _sourcePath, line: startLine, column: startCol);
            }
            Advance(); // closing quote
            return new Token(TokenKind.String, sb.ToString(), startLine, startCol);
        }

        private Token LexInteger(int startLine, int startCol)
        {
            int start = _pos;
            while (_pos < _src.Length && char.IsDigit(_src[_pos]))
            {
                Advance();
            }
            return new Token(TokenKind.Integer, _src[start.._pos], startLine, startCol);
        }

        private Token LexIdentifierOrKeyword(int startLine, int startCol)
        {
            int start = _pos;
            while (_pos < _src.Length && IsIdentCont(_src[_pos]))
            {
                Advance();
            }
            string text = _src[start.._pos];
            TokenKind kind = text switch
            {
                "and" => TokenKind.KwAnd,
                "or" => TokenKind.KwOr,
                "not" => TokenKind.KwNot,
                "if" => TokenKind.KwIf,
                "else" => TokenKind.KwElse,
                "in" => TokenKind.KwIn,
                "True" or "False" => TokenKind.BoolLiteral,
                "None" => TokenKind.NoneLiteral,
                _ => TokenKind.Identifier,
            };
            return new Token(kind, text, startLine, startCol);
        }

        private bool TryMatch(string needle)
        {
            if (_pos + needle.Length > _src.Length)
            {
                return false;
            }
            for (int i = 0; i < needle.Length; i++)
            {
                if (_src[_pos + i] != needle[i])
                {
                    return false;
                }
            }
            for (int i = 0; i < needle.Length; i++)
            {
                Advance();
            }
            return true;
        }

        private void Advance()
        {
            _pos++;
            _col++;
        }

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentCont(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    // -----------------------------------------------------------------
    // AST
    // -----------------------------------------------------------------

    private abstract record Node(int Line, int Column);
    private sealed record StringLit(string Value, int Line, int Column) : Node(Line, Column);
    private sealed record IntLit(long Value, int Line, int Column) : Node(Line, Column);
    private sealed record BoolLit(bool Value, int Line, int Column) : Node(Line, Column);
    private sealed record NoneLit(int Line, int Column) : Node(Line, Column);
    private sealed record QualifiedRef(IReadOnlyList<string> Path, int Line, int Column) : Node(Line, Column);
    private sealed record ListLit(IReadOnlyList<Node> Items, int Line, int Column) : Node(Line, Column);
    private sealed record UnaryNode(string Op, Node Inner, int Line, int Column) : Node(Line, Column);
    private sealed record BinaryNode(string Op, Node Lhs, Node Rhs, int Line, int Column) : Node(Line, Column);
    private sealed record InNode(bool Negate, Node Lhs, Node Rhs, int Line, int Column) : Node(Line, Column);
    private sealed record ConditionalNode(Node Then, Node Cond, Node Else, int Line, int Column) : Node(Line, Column);
    private sealed record CallNode(Node Receiver, string MethodName, IReadOnlyList<Node> Args, int Line, int Column) : Node(Line, Column);

    // -----------------------------------------------------------------
    // Parser
    // -----------------------------------------------------------------

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly string? _sourcePath;
        private int _pos;

        public Parser(List<Token> tokens, string? sourcePath)
        {
            _tokens = tokens;
            _sourcePath = sourcePath;
        }

        public Node ParseExpression() => ParseConditional();

        public void ExpectEof()
        {
            if (Peek().Kind != TokenKind.Eof)
            {
                Token t = Peek();
                throw new DescriptorParseException(
                    $"Unexpected token '{t.Text}' after end of expression.",
                    filePath: _sourcePath, line: t.Line, column: t.Column);
            }
        }

        private Node ParseConditional()
        {
            Node thenBranch = ParseOr();
            if (Peek().Kind == TokenKind.KwIf)
            {
                Token kw = Consume();
                Node cond = ParseOr();
                if (Peek().Kind != TokenKind.KwElse)
                {
                    Token t = Peek();
                    throw new DescriptorParseException(
                        "Expected 'else' to complete the conditional expression.",
                        filePath: _sourcePath, line: t.Line, column: t.Column);
                }
                Consume();
                Node elseBranch = ParseConditional();
                return new ConditionalNode(thenBranch, cond, elseBranch, kw.Line, kw.Column);
            }
            return thenBranch;
        }

        private Node ParseOr()
        {
            Node lhs = ParseAnd();
            while (Peek().Kind == TokenKind.KwOr)
            {
                Token op = Consume();
                Node rhs = ParseAnd();
                lhs = new BinaryNode("or", lhs, rhs, op.Line, op.Column);
            }
            return lhs;
        }

        private Node ParseAnd()
        {
            Node lhs = ParseNot();
            while (Peek().Kind == TokenKind.KwAnd)
            {
                Token op = Consume();
                Node rhs = ParseNot();
                lhs = new BinaryNode("and", lhs, rhs, op.Line, op.Column);
            }
            return lhs;
        }

        private Node ParseNot()
        {
            if (Peek().Kind == TokenKind.KwNot)
            {
                Token op = Consume();
                // Distinguish "not in" from prefix "not": if the next
                // token after `not` is `in`, this is part of a binary
                // operator and belongs to ParseComparison; otherwise,
                // it's a prefix.
                if (Peek().Kind == TokenKind.KwIn)
                {
                    throw new DescriptorParseException(
                        "Unexpected 'in' after 'not' in this position; 'not in' must follow a value.",
                        filePath: _sourcePath, line: Peek().Line, column: Peek().Column);
                }
                Node inner = ParseNot();
                return new UnaryNode("not", inner, op.Line, op.Column);
            }
            return ParseComparison();
        }

        private Node ParseComparison()
        {
            Node lhs = ParseUnary();
            while (true)
            {
                TokenKind k = Peek().Kind;
                if (k == TokenKind.Eq
                    || k == TokenKind.NEq
                    || k == TokenKind.Lt
                    || k == TokenKind.Gt
                    || k == TokenKind.LEq
                    || k == TokenKind.GEq)
                {
                    Token op = Consume();
                    Node rhs = ParseUnary();
                    lhs = new BinaryNode(op.Text, lhs, rhs, op.Line, op.Column);
                    continue;
                }
                if (k == TokenKind.KwIn)
                {
                    Token op = Consume();
                    Node rhs = ParseUnary();
                    lhs = new InNode(Negate: false, Lhs: lhs, Rhs: rhs, Line: op.Line, Column: op.Column);
                    continue;
                }
                if (k == TokenKind.KwNot && _pos + 1 < _tokens.Count && _tokens[_pos + 1].Kind == TokenKind.KwIn)
                {
                    Token op = Consume(); // not
                    Consume(); // in
                    Node rhs = ParseUnary();
                    lhs = new InNode(Negate: true, Lhs: lhs, Rhs: rhs, Line: op.Line, Column: op.Column);
                    continue;
                }
                return lhs;
            }
        }

        private Node ParseUnary()
        {
            if (Peek().Kind == TokenKind.Plus || Peek().Kind == TokenKind.Minus)
            {
                Token op = Consume();
                Node inner = ParseUnary();
                return new UnaryNode(op.Text, inner, op.Line, op.Column);
            }
            return ParseCall();
        }

        private Node ParseCall()
        {
            Node receiver = ParseAtom();
            while (Peek().Kind == TokenKind.Dot)
            {
                Token dot = Consume();
                if (Peek().Kind != TokenKind.Identifier)
                {
                    Token t = Peek();
                    throw new DescriptorParseException(
                        $"Expected identifier after '.' (got '{t.Text}').",
                        filePath: _sourcePath, line: t.Line, column: t.Column);
                }
                Token methodTok = Consume();
                if (Peek().Kind == TokenKind.LParen)
                {
                    Consume(); // (
                    List<Node> args = new();
                    if (Peek().Kind != TokenKind.RParen)
                    {
                        args.Add(ParseConditional());
                        while (Peek().Kind == TokenKind.Comma)
                        {
                            Consume();
                            args.Add(ParseConditional());
                        }
                    }
                    if (Peek().Kind != TokenKind.RParen)
                    {
                        Token t = Peek();
                        throw new DescriptorParseException(
                            "Expected ')' in method call argument list.",
                            filePath: _sourcePath, line: t.Line, column: t.Column);
                    }
                    Consume();
                    receiver = new CallNode(receiver, methodTok.Text, args, dot.Line, dot.Column);
                }
                else
                {
                    // Bare property chain (e.g. target.platform).
                    if (receiver is QualifiedRef existing)
                    {
                        List<string> combined = new(existing.Path) { methodTok.Text };
                        receiver = new QualifiedRef(combined, existing.Line, existing.Column);
                    }
                    else
                    {
                        Token t = methodTok;
                        throw new DescriptorParseException(
                            $"Dot-access without a call only valid on identifier chains (got after non-identifier value).",
                            filePath: _sourcePath, line: t.Line, column: t.Column);
                    }
                }
            }
            return receiver;
        }

        private Node ParseAtom()
        {
            Token t = Peek();
            switch (t.Kind)
            {
                case TokenKind.String:
                    Consume();
                    return new StringLit(t.Text, t.Line, t.Column);
                case TokenKind.Integer:
                    Consume();
                    if (!long.TryParse(t.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                    {
                        throw new DescriptorParseException(
                            $"Integer literal '{t.Text}' out of range.",
                            filePath: _sourcePath, line: t.Line, column: t.Column);
                    }
                    return new IntLit(n, t.Line, t.Column);
                case TokenKind.BoolLiteral:
                    Consume();
                    return new BoolLit(t.Text == "True", t.Line, t.Column);
                case TokenKind.NoneLiteral:
                    Consume();
                    return new NoneLit(t.Line, t.Column);
                case TokenKind.Identifier:
                    Consume();
                    // Function call (e.g. len(x)) or bare ref.
                    if (Peek().Kind == TokenKind.LParen)
                    {
                        Consume();
                        List<Node> args = new();
                        if (Peek().Kind != TokenKind.RParen)
                        {
                            args.Add(ParseConditional());
                            while (Peek().Kind == TokenKind.Comma)
                            {
                                Consume();
                                args.Add(ParseConditional());
                            }
                        }
                        if (Peek().Kind != TokenKind.RParen)
                        {
                            Token end = Peek();
                            throw new DescriptorParseException(
                                "Expected ')' in function call argument list.",
                                filePath: _sourcePath, line: end.Line, column: end.Column);
                        }
                        Consume();
                        return new CallNode(
                            Receiver: new NoneLit(t.Line, t.Column),  // sentinel: top-level call
                            MethodName: t.Text,
                            Args: args,
                            Line: t.Line,
                            Column: t.Column);
                    }
                    return new QualifiedRef(new[] { t.Text }, t.Line, t.Column);
                case TokenKind.LParen:
                    Consume();
                    Node inner = ParseConditional();
                    if (Peek().Kind != TokenKind.RParen)
                    {
                        Token end = Peek();
                        throw new DescriptorParseException(
                            "Expected ')' to close parenthesised expression.",
                            filePath: _sourcePath, line: end.Line, column: end.Column);
                    }
                    Consume();
                    return inner;
                case TokenKind.LBracket:
                    Consume();
                    List<Node> items = new();
                    if (Peek().Kind != TokenKind.RBracket)
                    {
                        items.Add(ParseConditional());
                        while (Peek().Kind == TokenKind.Comma)
                        {
                            Consume();
                            if (Peek().Kind == TokenKind.RBracket)
                            {
                                break; // trailing comma tolerated
                            }
                            items.Add(ParseConditional());
                        }
                    }
                    if (Peek().Kind != TokenKind.RBracket)
                    {
                        Token end = Peek();
                        throw new DescriptorParseException(
                            "Expected ']' to close list literal.",
                            filePath: _sourcePath, line: end.Line, column: end.Column);
                    }
                    Consume();
                    return new ListLit(items, t.Line, t.Column);
                default:
                    throw new DescriptorParseException(
                        $"Unexpected token '{t.Text}' in expression.",
                        filePath: _sourcePath, line: t.Line, column: t.Column);
            }
        }

        private Token Peek() => _tokens[_pos];

        private Token Consume() => _tokens[_pos++];
    }

    // -----------------------------------------------------------------
    // Interpreter
    // -----------------------------------------------------------------

    private sealed class Interpreter
    {
        private readonly Bindings _b;
        private readonly string? _sourcePath;
        private int _instructions;

        public Interpreter(Bindings b, string? sourcePath)
        {
            _b = b;
            _sourcePath = sourcePath;
        }

        public object Eval(Node n)
        {
            _instructions++;
            if (_instructions > InstructionBudget)
            {
                throw new DescriptorParseException(
                    $"Starlark evaluation budget of {InstructionBudget} instructions exceeded.",
                    filePath: _sourcePath, line: n.Line, column: n.Column);
            }

            switch (n)
            {
                case StringLit s: return s.Value;
                case IntLit i: return i.Value;
                case BoolLit b: return b.Value;
                case NoneLit: return Nothing.Instance;
                case QualifiedRef q: return ResolveRef(q);
                case ListLit l:
                    {
                        List<object> items = new(l.Items.Count);
                        foreach (Node item in l.Items)
                        {
                            items.Add(Eval(item));
                        }
                        return items;
                    }
                case UnaryNode u: return EvalUnary(u);
                case BinaryNode b: return EvalBinary(b);
                case InNode inN: return EvalIn(inN);
                case ConditionalNode c:
                    {
                        bool cond = ToBool(Eval(c.Cond), c.Cond);
                        return cond ? Eval(c.Then) : Eval(c.Else);
                    }
                case CallNode call: return EvalCall(call);
                default:
                    throw new DescriptorParseException(
                        $"Unsupported AST node type {n.GetType().Name}.",
                        filePath: _sourcePath, line: n.Line, column: n.Column);
            }
        }

        private object ResolveRef(QualifiedRef q)
        {
            // Allowed roots: target, module.
            if (q.Path.Count == 1)
            {
                // Bare identifier at expression scope without a call.
                // The grammar in this implementation does not bind any
                // bare top-level names; bare identifiers like
                // `extra_libs = ...` are parser-level assignments (not
                // implemented in this evaluator yet).
                throw new DescriptorParseException(
                    $"Unbound identifier '{q.Path[0]}'.",
                    filePath: _sourcePath, line: q.Line, column: q.Column);
            }
            string root = q.Path[0];
            string field = q.Path[1];
            object value = (root, field) switch
            {
                ("target", "platform") => _b.TargetPlatform,
                ("target", "configuration") => _b.TargetConfiguration,
                ("target", "station_role") => _b.TargetStationRole,
                ("target", "fips_mode") => (object)_b.TargetFipsMode,
                ("target", "architecture") => _b.TargetArchitecture,
                ("module", "tier") => _b.ModuleTier,
                ("module", "sim_path") => (object)_b.ModuleSimPath,
                ("module", "simd_level") => _b.ModuleSimdLevel,
                _ => throw new DescriptorParseException(
                    $"Unbound name '{string.Join(".", q.Path)}'. Allowed bindings: " +
                    "target.platform, target.configuration, target.station_role, target.fips_mode, " +
                    "target.architecture, module.tier, module.sim_path, module.simd_level.",
                    filePath: _sourcePath, line: q.Line, column: q.Column),
            };

            if (q.Path.Count > 2)
            {
                throw new DescriptorParseException(
                    $"Too-deep qualified reference '{string.Join(".", q.Path)}'. " +
                    "All bindings are exactly two-deep (target.X / module.X).",
                    filePath: _sourcePath, line: q.Line, column: q.Column);
            }
            return value;
        }

        private object EvalUnary(UnaryNode u)
        {
            object v = Eval(u.Inner);
            return u.Op switch
            {
                "not" => !ToBool(v, u.Inner),
                "+" => v is long n ? n : throw new DescriptorParseException(
                    "Unary '+' requires an integer operand.",
                    filePath: _sourcePath, line: u.Line, column: u.Column),
                "-" => v is long nm ? -nm : throw new DescriptorParseException(
                    "Unary '-' requires an integer operand.",
                    filePath: _sourcePath, line: u.Line, column: u.Column),
                _ => throw new DescriptorParseException(
                    $"Unknown unary operator '{u.Op}'.",
                    filePath: _sourcePath, line: u.Line, column: u.Column),
            };
        }

        private object EvalBinary(BinaryNode b)
        {
            // Short-circuit and/or before evaluating rhs.
            if (b.Op == "and")
            {
                object l = Eval(b.Lhs);
                if (!ToBool(l, b.Lhs)) { return false; }
                object r = Eval(b.Rhs);
                return ToBool(r, b.Rhs);
            }
            if (b.Op == "or")
            {
                object l = Eval(b.Lhs);
                if (ToBool(l, b.Lhs)) { return true; }
                object r = Eval(b.Rhs);
                return ToBool(r, b.Rhs);
            }

            object lhs = Eval(b.Lhs);
            object rhs = Eval(b.Rhs);
            return b.Op switch
            {
                "==" => ScalarEquals(lhs, rhs),
                "!=" => !ScalarEquals(lhs, rhs),
                "<" => CompareScalars(lhs, rhs, b) < 0,
                ">" => CompareScalars(lhs, rhs, b) > 0,
                "<=" => CompareScalars(lhs, rhs, b) <= 0,
                ">=" => CompareScalars(lhs, rhs, b) >= 0,
                _ => throw new DescriptorParseException(
                    $"Unknown binary operator '{b.Op}'.",
                    filePath: _sourcePath, line: b.Line, column: b.Column),
            };
        }

        private object EvalIn(InNode inN)
        {
            object lhs = Eval(inN.Lhs);
            object rhs = Eval(inN.Rhs);
            bool found;
            switch (rhs)
            {
                case List<object> list:
                    found = false;
                    foreach (object item in list)
                    {
                        if (ScalarEquals(lhs, item))
                        {
                            found = true;
                            break;
                        }
                    }
                    break;
                case string s when lhs is string needle:
                    found = s.Contains(needle, StringComparison.Ordinal);
                    break;
                default:
                    throw new DescriptorParseException(
                        $"'in' requires a list rhs or string-in-string; got {DescribeType(lhs)} in {DescribeType(rhs)}.",
                        filePath: _sourcePath, line: inN.Line, column: inN.Column);
            }
            return inN.Negate ? !found : found;
        }

        private object EvalCall(CallNode call)
        {
            // Top-level function calls: len() is the only built-in.
            if (call.Receiver is NoneLit)
            {
                if (call.MethodName == "len")
                {
                    if (call.Args.Count != 1)
                    {
                        throw new DescriptorParseException(
                            $"len() takes exactly one argument; got {call.Args.Count}.",
                            filePath: _sourcePath, line: call.Line, column: call.Column);
                    }
                    object arg = Eval(call.Args[0]);
                    return arg switch
                    {
                        string s => (long)s.Length,
                        List<object> lst => (long)lst.Count,
                        _ => throw new DescriptorParseException(
                            $"len() requires string or list; got {DescribeType(arg)}.",
                            filePath: _sourcePath, line: call.Line, column: call.Column),
                    };
                }
                throw new DescriptorParseException(
                    $"Unknown function '{call.MethodName}'. Only len() is in the subset.",
                    filePath: _sourcePath, line: call.Line, column: call.Column);
            }

            // Method call on a value.
            object recv = Eval(call.Receiver);
            return (recv, call.MethodName) switch
            {
                (string s, "startswith") when call.Args.Count == 1 => s.StartsWith(ExpectString(Eval(call.Args[0]), call), StringComparison.Ordinal),
                (string s, "endswith") when call.Args.Count == 1 => s.EndsWith(ExpectString(Eval(call.Args[0]), call), StringComparison.Ordinal),
                (string s, "contains") when call.Args.Count == 1 => s.Contains(ExpectString(Eval(call.Args[0]), call), StringComparison.Ordinal),
                (string s, "lower") when call.Args.Count == 0 => s.ToLowerInvariant(),
                (string s, "upper") when call.Args.Count == 0 => s.ToUpperInvariant(),
                _ => throw new DescriptorParseException(
                    $"Method '{call.MethodName}' not supported on {DescribeType(recv)} (or wrong argcount).",
                    filePath: _sourcePath, line: call.Line, column: call.Column),
            };
        }

        private string ExpectString(object v, CallNode call)
        {
            if (v is string s)
            {
                return s;
            }
            throw new DescriptorParseException(
                $"String method on {DescribeType(v)} (expected string argument).",
                filePath: _sourcePath, line: call.Line, column: call.Column);
        }

        private bool ToBool(object v, Node origin) => v switch
        {
            bool b => b,
            _ => throw new DescriptorParseException(
                $"Expected boolean (got {DescribeType(v)}). Implicit truthy/falsy is not in the subset.",
                filePath: _sourcePath, line: origin.Line, column: origin.Column),
        };

        private static bool ScalarEquals(object a, object b) => (a, b) switch
        {
            (string sa, string sb) => sa == sb,
            (long la, long lb) => la == lb,
            (bool ba, bool bb) => ba == bb,
            _ => false,
        };

        private int CompareScalars(object a, object b, BinaryNode origin)
        {
            return (a, b) switch
            {
                (long la, long lb) => la.CompareTo(lb),
                (string sa, string sb) => string.CompareOrdinal(sa, sb),
                _ => throw new DescriptorParseException(
                    $"Operator '{origin.Op}' requires matching scalar types (got {DescribeType(a)} and {DescribeType(b)}).",
                    filePath: _sourcePath, line: origin.Line, column: origin.Column),
            };
        }

        private static string DescribeType(object v) => v switch
        {
            string => "string",
            long => "integer",
            bool => "boolean",
            List<object> => "list",
            Nothing => "None",
            _ => v.GetType().Name,
        };
    }

    /// <summary>
    /// Singleton sentinel used to represent Starlark's <c>None</c> in
    /// the evaluator's output. The interpreter handles it explicitly;
    /// callers that receive a return value from <see cref="Evaluate"/>
    /// can match against this type if they need to discriminate
    /// "explicit None" from a missing key.
    /// </summary>
    public sealed class Nothing
    {
        /// <summary>The singleton instance.</summary>
        public static readonly Nothing Instance = new();
        private Nothing() { }
    }
}
