namespace IZLang.Lexing
{
    public enum TokenKind
    {
        // structure
        EndOfFile,
        Bad,

        // literals
        Number,
        String,
        HashLiteral,      // #"StructureWallLight"
        Identifier,

        // keywords
        KwVar, KwConst, KwDevice, KwFn, KwReturn, KwStruct,
        KwIf, KwElse, KwWhile, KwLoop, KwFor, KwIn,
        KwBreak, KwContinue, KwYield,
        KwTrue, KwFalse,
        KwNum, KwBool, KwStr, KwDev,      // type names
        KwList,                            // 'list num[8]'
        KwAll, KwNamed,                    // batch selectors

        // punctuation
        LParen, RParen,
        LBrace, RBrace,
        LBracket, RBracket,
        Comma, Semicolon, Colon, Dot,
        Question,                          // the '?' of the ternary operator
        DotDot, DotDotEquals,              // 0..10 and 0..=10
        Arrow,                             // ->
        FatArrow,                          // =>, the body of a query lambda

        // operators
        Plus, Minus, Star, Slash, Percent,
        Amp, Pipe, Caret, Tilde,
        AmpAmp, PipePipe, Bang,
        LessLess, GreaterGreater,
        Less, LessEquals, Greater, GreaterEquals,
        EqualsEquals, BangEquals,

        // assignment
        Equals,
        PlusEquals, MinusEquals, StarEquals, SlashEquals, PercentEquals,
    }
}
