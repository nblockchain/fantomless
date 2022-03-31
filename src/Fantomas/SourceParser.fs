module internal Fantomas.SourceParser

open System
open FSharp.Compiler.Syntax
open FSharp.Compiler.Syntax.PrettyNaming
open FSharp.Compiler.Text
open FSharp.Compiler.Xml
open Fantomas
open Fantomas.TriviaTypes
open Fantomas.RangePatterns

/// Don't put space before and after these operators
let internal noSpaceInfixOps = set [ "?" ]

/// Always break into newlines on these operators
let internal newLineInfixOps = set [ "|>"; "||>"; "|||>"; ">>"; ">>=" ]

/// Never break into newlines on these operators
let internal noBreakInfixOps = set [ "="; ">"; "<"; "%" ]

type Composite<'a, 'b> =
    | Pair of 'b * 'b
    | Single of 'a

[<Literal>]
let MaxLength = 512

[<Literal>]
let private MangledGlobalName: string = "`global`"

let (|Ident|) (s: Ident) =
    let ident = s.idText

    match ident with
    | "`global`" -> "global"
    | "not" -> "not"
    | "params" -> "``params``"
    | "parallel" -> "``parallel``"
    | "mod" -> "``mod``"
    | _ ->
        if IsActivePatternName ident then
            sprintf "(%s)" (DecompileOpName ident)
        else
            AddBackticksToIdentifierIfNeeded ident

let (|LongIdent|) (li: LongIdent) =
    li
    |> Seq.map (fun x ->
        if x.idText = MangledGlobalName then
            "global"
        else
            (|Ident|) x)
    |> String.concat "."
    |> fun s ->
        // Assume that if it starts with base, it's going to be the base keyword
        if String.startsWithOrdinal "``base``." s then
            String.Join("", "base.", s.[9..])
        else
            s

let (|LongIdentPieces|) (li: LongIdent) =
    li
    |> List.map (fun x ->
        if x.idText = MangledGlobalName then
            "global", x.idRange
        else
            (|Ident|) x, x.idRange)

let (|LongIdentPiecesExpr|_|) =
    function
    | SynExpr.LongIdent (_, LongIdentWithDots (lids, _), _, _) ->
        lids
        |> List.map (fun x ->
            if x.idText = MangledGlobalName then
                "global", x.idRange
            else
                (|Ident|) x, x.idRange)
        |> Some
    | _ -> None


let (|LongIdentWithDotsPieces|_|) =
    function
    | LongIdentWithDots (lids, _) ->
        lids
        |> List.map (fun x ->
            if x.idText = MangledGlobalName then
                "global", x.idRange
            else
                (|Ident|) x, x.idRange)
        |> Some

let inline (|LongIdentWithDots|) (LongIdentWithDots (LongIdent s, _)) = s

type Identifier =
    | Id of Ident
    | LongId of LongIdent
    member x.Text =
        match x with
        | Id x -> x.idText
        | LongId xs ->
            xs
            |> Seq.map (fun x ->
                if x.idText = MangledGlobalName then
                    "global"
                else
                    x.idText)
            |> String.concat "."

    member x.Ranges =
        match x with
        | Id x -> List.singleton x.idRange
        | LongId xs -> List.map (fun (x: Ident) -> x.idRange) xs

/// Different from (|Ident|), this pattern also accepts keywords
let inline (|IdentOrKeyword|) (s: Ident) = Id s

let (|LongIdentOrKeyword|) (li: LongIdent) = LongId li

/// Use infix operators in the short form
let (|OpName|) (x: Identifier) =
    let s = x.Text
    let s' = DecompileOpName s

    if IsActivePatternName s then
        sprintf "(%s)" s'
    elif IsPrefixOperator s then
        if s'.[0] = '~' && s'.Length >= 2 && s'.[1] <> '~' then
            s'.[1..]
        else
            s'
    else
        match x with
        | Id (Ident s)
        | LongId (LongIdent s) -> DecompileOpName s

let (|OpNameFullInPattern|) (x: Identifier) =
    let r = x.Ranges
    let s = x.Text
    let s' = DecompileOpName s

    (if IsActivePatternName s
        || IsMangledInfixOperator s
        || IsPrefixOperator s
        || IsTernaryOperator s
        || s = "op_Dynamic" then
         // Use two spaces for symmetry
         if String.startsWithOrdinal "*" s' && s' <> "*" then
             sprintf "( %s )" s'
         else
             sprintf "(%s)" s'
     else
         match x with
         | Id (Ident s)
         | LongId (LongIdent s) -> DecompileOpName s)
    |> fun s -> (s, r)


/// Operators in their declaration form
let (|OpNameFull|) (x: Identifier) =
    let r = x.Ranges
    let s = x.Text
    let s' = DecompileOpName s

    (if IsActivePatternName s then
         s
     elif IsMangledInfixOperator s
          || IsPrefixOperator s
          || IsTernaryOperator s
          || s = "op_Dynamic" then
         // Use two spaces for symmetry
         if String.startsWithOrdinal "*" s' && s' <> "*" then
             sprintf " %s " s'
         else
             s'
     else
         match x with
         | Id (Ident s)
         | LongId (LongIdent s) -> DecompileOpName s)
    |> fun s -> (s, r)

// Type params

let inline (|Typar|) (SynTypar.SynTypar (Ident s as ident, req, _)) =
    match req with
    | TyparStaticReq.None -> (s, ident.idRange, false)
    | TyparStaticReq.HeadType -> (s, ident.idRange, true)

let inline (|ValTyparDecls|) (SynValTyparDecls (tds, b)) = (tds, b)

// Literals

let rec (|RationalConst|) =
    function
    | SynRationalConst.Integer i -> string i
    | SynRationalConst.Rational (numerator, denominator, _) -> sprintf "(%i/%i)" numerator denominator
    | SynRationalConst.Negate (RationalConst s) -> sprintf "- %s" s

let (|Measure|) x =
    let rec loop =
        function
        | SynMeasure.Var (Typar (s, _, _), _) -> s
        | SynMeasure.Anon _ -> "_"
        | SynMeasure.One -> "1"
        | SynMeasure.Product (m1, m2, _) ->
            let s1 = loop m1
            let s2 = loop m2
            sprintf "%s*%s" s1 s2
        | SynMeasure.Divide (m1, m2, _) ->
            let s1 = loop m1
            let s2 = loop m2
            sprintf "%s/%s" s1 s2
        | SynMeasure.Power (m, RationalConst n, _) ->
            let s = loop m
            sprintf "%s^%s" s n
        | SynMeasure.Seq (ms, _) -> List.map loop ms |> String.concat " "
        | SynMeasure.Named (LongIdent s, _) -> s

    sprintf "<%s>" <| loop x

let (|String|_|) e =
    match e with
    | SynExpr.Const (SynConst.String (s, _, _), _) -> Some s
    | _ -> None

let (|Unit|_|) =
    function
    | SynConst.Unit _ -> Some()
    | _ -> None

// File level patterns

let (|ImplFile|SigFile|) =
    function
    | ParsedInput.ImplFile im -> ImplFile im
    | ParsedInput.SigFile si -> SigFile si

let (|ParsedImplFileInput|) (ParsedImplFileInput.ParsedImplFileInput (_, _, _, _, hs, mns, _)) = (hs, mns)

let (|ParsedSigFileInput|) (ParsedSigFileInput.ParsedSigFileInput (_, _, _, hs, mns)) = (hs, mns)

let (|ModuleOrNamespace|)
    (SynModuleOrNamespace.SynModuleOrNamespace (LongIdentPieces lids, isRecursive, kind, mds, px, ats, ao, range))
    =
    (ats, px, ao, lids, mds, isRecursive, kind, range)

let (|SigModuleOrNamespace|)
    (SynModuleOrNamespaceSig.SynModuleOrNamespaceSig (LongIdentPieces lids, isRecursive, kind, mds, px, ats, ao, range))
    =
    (ats, px, ao, lids, mds, isRecursive, kind, range)

let (|EmptyFile|_|) (input: ParsedInput) =
    match input with
    | ImplFile (ParsedImplFileInput (_,
                                     [ ModuleOrNamespace (_, _, _, _, [], _, SynModuleOrNamespaceKind.AnonModule, _) ])) ->
        Some input
    | SigFile (ParsedSigFileInput (_,
                                   [ SigModuleOrNamespace (_, _, _, _, [], _, SynModuleOrNamespaceKind.AnonModule, _) ])) ->
        Some input
    | _ -> None

let (|Attribute|) (a: SynAttribute) =
    let (LongIdentWithDots s) = a.TypeName
    (s, a.ArgExpr, Option.map (fun (i: Ident) -> i.idText) a.Target)

// Access modifiers

let (|Access|) =
    function
    | SynAccess.Public -> "public"
    | SynAccess.Internal -> "internal"
    | SynAccess.Private -> "private"

let (|PreXmlDoc|) (px: PreXmlDoc) =
    let xmlDoc = px.ToXmlDoc(false, None)
    xmlDoc.UnprocessedLines, xmlDoc.Range

// Module declarations (11 cases)
let (|Open|_|) =
    function
    | SynModuleDecl.Open (SynOpenDeclTarget.ModuleOrNamespace (LongIdent s, _m), _) -> Some s
    | _ -> None

let (|OpenType|_|) =
    function
    // TODO: are there other SynType causes that need to be handled here?
    | SynModuleDecl.Open (SynOpenDeclTarget.Type (SynType.LongIdent (LongIdentWithDots s), _m), _) -> Some s
    | _ -> None

let (|ModuleAbbrev|_|) =
    function
    | SynModuleDecl.ModuleAbbrev (Ident s1, LongIdent s2, _) -> Some(s1, s2)
    | _ -> None

let (|HashDirective|_|) =
    function
    | SynModuleDecl.HashDirective (p, _) -> Some p
    | _ -> None

let (|NamespaceFragment|_|) =
    function
    | SynModuleDecl.NamespaceFragment m -> Some m
    | _ -> None

let (|Attributes|_|) =
    function
    | SynModuleDecl.Attributes (ats, _) -> Some(ats)
    | _ -> None

let (|Let|_|) =
    function
    | SynModuleDecl.Let (false, [ x ], _) -> Some x
    | _ -> None

let (|LetRec|_|) =
    function
    | SynModuleDecl.Let (true, xs, _) -> Some xs
    | _ -> None

let (|DoExpr|_|) =
    function
    | SynModuleDecl.DoExpr (_, x, _) -> Some x
    | _ -> None

let (|Types|_|) =
    function
    | SynModuleDecl.Types (xs, _) -> Some xs
    | _ -> None

let (|NestedModule|_|) =
    function
    | SynModuleDecl.NestedModule (SynComponentInfo (ats, _, _, LongIdent s, px, _, ao, _), isRecursive, xs, _, _, trivia) ->
        Some(ats, px, trivia.ModuleKeyword, ao, s, isRecursive, trivia.EqualsRange, xs)
    | _ -> None

let (|Exception|_|) =
    function
    | SynModuleDecl.Exception (ed, _) -> Some ed
    | _ -> None

// Module declaration signatures (9 cases)

let (|SigOpen|_|) =
    function
    | SynModuleSigDecl.Open (SynOpenDeclTarget.ModuleOrNamespace (LongIdent s, _), _) -> Some s
    | _ -> None

let (|SigOpenType|_|) =
    function
    | SynModuleSigDecl.Open (SynOpenDeclTarget.Type (SynType.LongIdent (LongIdentWithDots s), _), _) -> Some s
    | _ -> None

let (|SigModuleAbbrev|_|) =
    function
    | SynModuleSigDecl.ModuleAbbrev (Ident s1, LongIdent s2, _) -> Some(s1, s2)
    | _ -> None

let (|SigHashDirective|_|) =
    function
    | SynModuleSigDecl.HashDirective (p, _) -> Some p
    | _ -> None

let (|SigNamespaceFragment|_|) =
    function
    | SynModuleSigDecl.NamespaceFragment m -> Some m
    | _ -> None

let (|SigVal|_|) =
    function
    | SynModuleSigDecl.Val (v, _) -> Some v
    | _ -> None

let (|SigTypes|_|) =
    function
    | SynModuleSigDecl.Types (tds, _) -> Some tds
    | _ -> None

let (|SigNestedModule|_|) =
    function
    | SynModuleSigDecl.NestedModule (SynComponentInfo (ats, _, _, LongIdent s, px, _, ao, _), _, xs, _, trivia) ->
        Some(ats, px, trivia.ModuleKeyword, ao, s, trivia.EqualsRange, xs)
    | _ -> None

let (|SigException|_|) =
    function
    | SynModuleSigDecl.Exception (es, _) -> Some es
    | _ -> None

// Exception definitions

let (|ExceptionDefRepr|) (SynExceptionDefnRepr.SynExceptionDefnRepr (ats, uc, _, px, ao, _)) = (ats, px, ao, uc)

let (|SigExceptionDefRepr|) (SynExceptionDefnRepr.SynExceptionDefnRepr (ats, uc, _, px, ao, _)) = (ats, px, ao, uc)

let (|ExceptionDef|)
    (SynExceptionDefn.SynExceptionDefn (SynExceptionDefnRepr.SynExceptionDefnRepr (ats, uc, _, px, ao, _),
                                        withKeyword,
                                        ms,
                                        _))
    =
    (ats, px, ao, uc, withKeyword, ms)

let (|SigExceptionDef|)
    (SynExceptionSig.SynExceptionSig (SynExceptionDefnRepr.SynExceptionDefnRepr (ats, uc, _, px, ao, _),
                                      withKeyword,
                                      ms,
                                      _))
    =
    (ats, px, ao, uc, withKeyword, ms)

let (|UnionCase|) (SynUnionCase (ats, Ident s, uct, px, ao, _, trivia)) = (ats, px, ao, s, uct)

let (|UnionCaseType|) =
    function
    | SynUnionCaseKind.Fields fs -> fs
    | SynUnionCaseKind.FullType _ -> failwith "UnionCaseFullType should be used internally only."

let (|Field|) (SynField (ats, isStatic, ido, t, isMutable, px, ao, range)) =
    let innerRange =
        ido
        |> Option.map (fun i -> Range.unionRanges i.idRange t.Range)

    (ats, px, ao, isStatic, isMutable, t, Option.map (|Ident|) ido, innerRange, range)

let (|EnumCase|) (SynEnumCase (ats, Ident s, c, cr, px, r, trivia)) =
    (ats, trivia.BarRange, px, s, trivia.EqualsRange, c, cr, r)

// Member definitions (11 cases)

let (|MDNestedType|_|) =
    function
    | SynMemberDefn.NestedType (td, ao, _) -> Some(td, ao)
    | _ -> None

let (|MDOpen|_|) =
    function
    | SynMemberDefn.Open (SynOpenDeclTarget.ModuleOrNamespace (LongIdent s, _), _) -> Some s
    | _ -> None

let (|MDOpenType|_|) =
    function
    | SynMemberDefn.Open (SynOpenDeclTarget.Type (SynType.LongIdent (LongIdentWithDots s), _), _) -> Some s
    | _ -> None

let (|MDImplicitInherit|_|) =
    function
    | SynMemberDefn.ImplicitInherit (t, e, ido, _) -> Some(t, e, Option.map (|Ident|) ido)
    | _ -> None

let (|MDInherit|_|) =
    function
    | SynMemberDefn.Inherit (t, ido, _) -> Some(t, Option.map (|Ident|) ido)
    | _ -> None

let (|MDValField|_|) =
    function
    | SynMemberDefn.ValField (f, _) -> Some f
    | _ -> None

let (|MDImplicitCtor|_|) =
    function
    | SynMemberDefn.ImplicitCtor (ao, ats, ps, ido, docs, _) -> Some(docs, ats, ao, ps, Option.map (|Ident|) ido)
    | _ -> None

let (|MDMember|_|) =
    function
    | SynMemberDefn.Member (b, _) -> Some b
    | _ -> None

let (|MDLetBindings|_|) =
    function
    | SynMemberDefn.LetBindings (es, isStatic, isRec, _) -> Some(isStatic, isRec, es)
    | _ -> None

let (|MDAbstractSlot|_|) =
    function
    | SynMemberDefn.AbstractSlot (SynValSig (ats, Ident s, tds, t, vi, _, _, px, ao, _, _, _), mf, _) ->
        Some(ats, px, ao, s, t, vi, tds, mf)
    | _ -> None

let (|MDInterface|_|) =
    function
    | SynMemberDefn.Interface (t, withKeyword, mdo, range) -> Some(t, withKeyword, mdo, range)
    | _ -> None

let (|MDAutoProperty|_|) =
    function
    | SynMemberDefn.AutoProperty (ats,
                                  isStatic,
                                  Ident s,
                                  typeOpt,
                                  mk,
                                  memberKindToMemberFlags,
                                  px,
                                  ao,
                                  equalsRange,
                                  e,
                                  withKeyword,
                                  _,
                                  _) ->
        Some(ats, px, ao, mk, equalsRange, e, withKeyword, s, isStatic, typeOpt, memberKindToMemberFlags)
    | _ -> None

// Interface impl

let (|InterfaceImpl|) (SynInterfaceImpl (t, withKeywordRange, bs, members, range)) =
    (t, withKeywordRange, bs, members, range)

// Bindings

let (|PropertyGet|_|) =
    function
    | SynMemberKind.PropertyGet -> Some()
    | _ -> None

let (|PropertySet|_|) =
    function
    | SynMemberKind.PropertySet -> Some()
    | _ -> None

let (|PropertyGetSet|_|) =
    function
    | SynMemberKind.PropertyGetSet -> Some()
    | _ -> None

let (|MFProperty|_|) (mf: SynMemberFlags) =
    match mf.MemberKind with
    | SynMemberKind.PropertyGet
    | SynMemberKind.PropertySet
    | SynMemberKind.PropertyGetSet as mk -> Some mk
    | _ -> None

/// This pattern finds out which keyword to use
let (|MFMember|MFStaticMember|MFConstructor|MFOverride|) (mf: SynMemberFlags) =
    match mf.MemberKind with
    | SynMemberKind.ClassConstructor
    | SynMemberKind.Constructor -> MFConstructor()
    | SynMemberKind.Member
    | SynMemberKind.PropertyGet
    | SynMemberKind.PropertySet
    | SynMemberKind.PropertyGetSet as mk ->
        if mf.IsInstance && mf.IsOverrideOrExplicitImpl then
            MFOverride mk
        elif mf.IsInstance then
            MFMember mk
        else
            MFStaticMember mk

let (|DoBinding|LetBinding|MemberBinding|PropertyBinding|ExplicitCtor|) =
    function
    | SynBinding (ao, _, _, _, ats, px, SynValData (Some MFConstructor, _, ido), pat, _, expr, _, _, trivia) ->
        ExplicitCtor(ats, px, ao, pat, trivia.EqualsRange, expr, Option.map (|Ident|) ido)
    | SynBinding (ao,
                  _,
                  isInline,
                  _,
                  ats,
                  px,
                  SynValData (Some (MFProperty _ as mf), synValInfo, _),
                  pat,
                  _,
                  expr,
                  _,
                  _,
                  trivia) -> PropertyBinding(ats, px, ao, isInline, mf, pat, trivia.EqualsRange, expr, synValInfo)
    | SynBinding (ao, _, isInline, _, ats, px, SynValData (Some mf, synValInfo, _), pat, _, expr, _, _, trivia) ->
        MemberBinding(ats, px, ao, isInline, mf, pat, trivia.EqualsRange, expr, synValInfo)
    | SynBinding (_, SynBindingKind.Do, _, _, ats, px, _, _, _, expr, _, _, trivia) -> DoBinding(ats, px, expr)
    | SynBinding (ao, _, isInline, isMutable, attrs, px, SynValData (_, valInfo, _), pat, _, expr, _, _, trivia) ->
        LetBinding(attrs, px, trivia.LetKeyword, ao, isInline, isMutable, pat, trivia.EqualsRange, expr, valInfo)

// Expressions (55 cases, lacking to handle 11 cases)

let (|TraitCall|_|) =
    function
    | SynExpr.TraitCall (tps, msg, expr, _) -> Some(tps, msg, expr)
    | _ -> None

/// isRaw = true with <@@ and @@>
let (|Quote|_|) =
    function
    | SynExpr.Quote (e1, isRaw, e2, _, _) -> Some(e1, e2, isRaw)
    | _ -> None

let (|Paren|_|) =
    function
    | SynExpr.Paren (e, lpr, rpr, r) -> Some(lpr, e, rpr, r)
    | _ -> None

let (|LazyExpr|_|) (e: SynExpr) =
    match e with
    | SynExpr.Lazy (e, StartRange 4 (lazyKeyword, _range)) -> Some(lazyKeyword, e)
    | _ -> None

type ExprKind =
    | InferredDowncast of keyword: range
    | InferredUpcast of keyword: range
    | Assert of keyword: range
    | AddressOfSingle of token: range
    | AddressOfDouble of token: range
    | Yield of keyword: range
    | Return of keyword: range
    | YieldFrom of keyword: range
    | ReturnFrom of keyword: range
    | Do of keyword: range
    | DoBang of Keyword: range
    | Fixed of keyword: range

let (|SingleExpr|_|) =
    function
    | SynExpr.InferredDowncast (e, StartRange 8 (downcastKeyword, _range)) -> Some(InferredDowncast downcastKeyword, e)
    | SynExpr.InferredUpcast (e, StartRange 6 (upcastKeyword, _range)) -> Some(InferredUpcast upcastKeyword, e)
    | SynExpr.Assert (e, StartRange 6 (assertKeyword, _range)) -> Some(Assert assertKeyword, e)
    | SynExpr.AddressOf (true, e, _, StartRange 1 (ampersandToken, _range)) -> Some(AddressOfSingle ampersandToken, e)
    | SynExpr.AddressOf (false, e, _, StartRange 2 (ampersandToken, _range)) -> Some(AddressOfDouble ampersandToken, e)
    | SynExpr.YieldOrReturn ((true, _), e, StartRange 5 (yieldKeyword, _range)) -> Some(Yield yieldKeyword, e)
    | SynExpr.YieldOrReturn ((false, _), e, StartRange 6 (returnKeyword, _range)) -> Some(Return returnKeyword, e)
    | SynExpr.YieldOrReturnFrom ((true, _), e, StartRange 6 (yieldBangKeyword, _range)) ->
        Some(YieldFrom yieldBangKeyword, e)
    | SynExpr.YieldOrReturnFrom ((false, _), e, StartRange 7 (returnBangKeyword, _range)) ->
        Some(ReturnFrom returnBangKeyword, e)
    | SynExpr.Do (e, StartRange 2 (doKeyword, _range)) -> Some(Do doKeyword, e)
    | SynExpr.DoBang (e, StartRange 3 (doBangKeyword, _range)) -> Some(DoBang doBangKeyword, e)
    | SynExpr.Fixed (e, StartRange 5 (fixedKeyword, _range)) -> Some(Fixed fixedKeyword, e)
    | _ -> None

type TypedExprKind =
    | TypeTest
    | Downcast
    | Upcast
    | Typed

let (|TypedExpr|_|) =
    function
    | SynExpr.TypeTest (e, t, _) -> Some(TypeTest, e, t)
    | SynExpr.Downcast (e, t, _) -> Some(Downcast, e, t)
    | SynExpr.Upcast (e, t, _) -> Some(Upcast, e, t)
    | SynExpr.Typed (e, t, _) -> Some(Typed, e, t)
    | _ -> None

let (|While|_|) =
    function
    | SynExpr.While (_, e1, e2, _) -> Some(e1, e2)
    | _ -> None

let (|For|_|) =
    function
    | SynExpr.For (_, _, Ident s, equalsRange, e1, isUp, e2, e3, _) -> Some(s, equalsRange, e1, e2, e3, isUp)
    | _ -> None

let (|NullExpr|_|) =
    function
    | SynExpr.Null _ -> Some()
    | _ -> None

let (|ConstExpr|_|) =
    function
    | SynExpr.Const (x, r) -> Some(x, r)
    | _ -> None

let (|ConstUnitExpr|_|) =
    function
    | ConstExpr (Unit, _) -> Some()
    | _ -> None

let (|TypeApp|_|) =
    function
    | SynExpr.TypeApp (e, lessRange, ts, _, Some greaterRange, _, _range) -> Some(e, lessRange, ts, greaterRange)
    | _ -> None

let (|Match|_|) =
    function
    | SynExpr.Match (matchKeyword, _, e, withKeyword, cs, _) -> Some(matchKeyword, e, withKeyword, cs)
    | _ -> None

let (|MatchBang|_|) =
    function
    | SynExpr.MatchBang (matchKeyword, _, e, withKeyword, cs, _) -> Some(matchKeyword, e, withKeyword, cs)
    | _ -> None

let (|Sequential|_|) =
    function
    | SynExpr.Sequential (_, isSeq, e1, e2, _) -> Some(e1, e2, isSeq)
    | _ -> None

let rec (|Sequentials|_|) =
    function
    | Sequential (e, Sequentials es, _) -> Some(e :: es)
    | Sequential (e1, e2, _) -> Some [ e1; e2 ]
    | _ -> None

let (|SimpleExpr|_|) =
    function
    | SynExpr.Null _
    | SynExpr.Ident _
    | SynExpr.LongIdent _
    | SynExpr.Const _ as e -> Some e
    | _ -> None

let (|ComputationExpr|_|) =
    function
    | SynExpr.ComputationExpr (_, expr, StartEndRange 1 (openingBrace, _range, closingBrace)) ->
        Some(openingBrace, expr, closingBrace)
    | _ -> None

// seq { expr }
let (|NamedComputationExpr|_|) =
    function
    | SynExpr.App (ExprAtomicFlag.NonAtomic,
                   false,
                   (SynExpr.App _ as nameExpr
                   | SimpleExpr nameExpr),
                   ComputationExpr (openingBrace, compExpr, closingBrace),
                   _) -> Some(nameExpr, openingBrace, compExpr, closingBrace)
    | _ -> None

/// Combines all ArrayOrList patterns
let (|ArrayOrList|_|) =
    let (|Size|) isArray = if isArray then 2 else 1

    function
    | SynExpr.ArrayOrListComputed (Size size as isArray, Sequentials xs, range) ->
        let sr, er = RangeHelpers.mkStartEndRange size range
        Some(sr, isArray, xs, er, range)
    | SynExpr.ArrayOrListComputed (Size size as isArray, singleExpr, range) ->
        let sr, er = RangeHelpers.mkStartEndRange size range
        Some(sr, isArray, [ singleExpr ], er, range)
    | SynExpr.ArrayOrList (Size size as isArray, xs, range) ->
        let sr, er = RangeHelpers.mkStartEndRange size range
        Some(sr, isArray, xs, er, range)
    | _ -> None

let (|Tuple|_|) =
    function
    | SynExpr.Tuple (false, exprs, _, tupleRange) -> Some(exprs, tupleRange)
    | _ -> None

let (|StructTuple|_|) =
    function
    | SynExpr.Tuple (true, exprs, _, _) -> Some exprs
    | _ -> None

let (|IndexedVar|_|) =
    function
    // We might have to narrow scope of this pattern to avoid incorrect usage
    | SynExpr.App (_, _, SynExpr.LongIdent (_, LongIdentWithDots "Microsoft.FSharp.Core.Some", _, _), e, _) ->
        Some(Some e)
    | SynExpr.LongIdent (_, LongIdentWithDots "Microsoft.FSharp.Core.None", _, _) -> Some None
    | _ -> None

let (|InterpolatedStringExpr|_|) =
    function
    | SynExpr.InterpolatedString (parts, stringKind, _) -> Some(parts, stringKind)
    | _ -> None

let (|IndexRangeExpr|_|) =
    function
    | SynExpr.IndexRange (expr1, _, expr2, _, _, _) -> Some(expr1, expr2)
    | _ -> None

let (|IndexFromEndExpr|_|) =
    function
    | SynExpr.IndexFromEnd (e, _r) -> Some e
    | _ -> None

let (|ConstNumberExpr|_|) =
    function
    | ConstExpr (SynConst.Double _, _) as e -> Some e
    | ConstExpr (SynConst.Decimal _, _) as e -> Some e
    | ConstExpr (SynConst.Single _, _) as e -> Some e
    | ConstExpr (SynConst.Int16 _, _) as e -> Some e
    | ConstExpr (SynConst.Int32 _, _) as e -> Some e
    | ConstExpr (SynConst.Int64 _, _) as e -> Some e
    | _ -> None

let (|NegativeNumber|_|) =
    function
    | ConstExpr (SynConst.Double v, _) as e when v < 0. -> Some e
    | ConstExpr (SynConst.Decimal v, _) as e when v < 0M -> Some e
    | ConstExpr (SynConst.Single v, _) as e when v < 0f -> Some e
    | ConstExpr (SynConst.Int16 v, _) as e when v < 0s -> Some e
    | ConstExpr (SynConst.Int32 v, _) as e when v < 0 -> Some e
    | ConstExpr (SynConst.Int64 v, _) as e when v < 0L -> Some e
    | _ -> None

let (|OptVar|_|) =
    function
    | SynExpr.Ident (IdentOrKeyword (OpNameFull (s, r))) -> Some(s, false, r)
    | SynExpr.LongIdent (isOpt, LongIdentWithDots.LongIdentWithDots (LongIdentOrKeyword (OpNameFull (s, r)), _), _, _) ->
        Some(s, isOpt, r)
    | _ -> None

/// This pattern is escaped by using OpName
let (|Var|_|) =
    function
    | SynExpr.Ident (IdentOrKeyword (OpName s)) -> Some s
    | SynExpr.LongIdent (_, LongIdentWithDots.LongIdentWithDots (LongIdentOrKeyword (OpName s), _), _, _) -> Some s
    | _ -> None

// Compiler-generated patterns often have "_arg" prefix
let (|CompilerGeneratedVar|_|) =
    function
    | SynExpr.Ident (IdentOrKeyword (OpName s)) when String.startsWithOrdinal "_arg" s -> Some s
    | SynExpr.LongIdent (_, LongIdentWithDots.LongIdentWithDots (LongIdentOrKeyword (OpName s), _), opt, _) ->
        match opt with
        | Some _ -> Some s
        | None ->
            if String.startsWithOrdinal "_arg" s then
                Some s
            else
                None
    | _ -> None

/// Get all application params at once
let (|App|_|) e =
    let rec loop =
        function
        // function application is left-recursive
        | SynExpr.App (_, _, e, e2, _) ->
            let e1, es = loop e
            (e1, e2 :: es)
        | e -> (e, [])

    match loop e with
    | _, [] -> None
    | e, es -> Some(e, List.rev es)

// captures application with single parenthesis argument
let (|AppSingleParenArg|_|) =
    function
    | App (SynExpr.DotGet _, [ (Paren (_, Tuple _, _, _)) ]) -> None
    | App (e, [ Paren (_, singleExpr, _, _) as px ]) ->
        match singleExpr with
        | SynExpr.Lambda _
        | SynExpr.MatchLambda _ -> None
        | _ -> Some(e, px)
    | _ -> None

let (|RaiseApp|_|) =
    function
    | App (e1, [ Paren (lpr, e2, rpr, _) ]) ->
        match e1 with
        | SynExpr.Ident (Ident id) when id.Equals "raise" ->
            match e2 with
            | SynExpr.Lambda _
            | SynExpr.MatchLambda _ -> None
            | SynExpr.New _ -> Some(e1, e2, true, lpr, rpr)
            | _ -> Some(e1, e2, false, lpr, rpr)
        | _ -> None
    | _ -> None

let (|AppOrTypeApp|_|) e =
    match e with
    | App (TypeApp (e, lt, ts, gt), es) -> Some(e, Some(lt, ts, gt), es)
    | App (e, es) -> Some(e, None, es)
    | _ -> None

let (|NewTuple|_|) =
    function
    | SynExpr.New (_, t, (Paren _ as px), _) -> Some(t, px)
    | SynExpr.New (_, t, (ConstExpr (SynConst.Unit, _) as px), _) -> Some(t, px)
    | _ -> None

/// Only process prefix operators here
let (|PrefixApp|_|) =
    function
    // Var pattern causes a few prefix operators appear as infix operators
    | SynExpr.App (_, false, SynExpr.Ident (IdentOrKeyword s), e2, _)
    | SynExpr.App (_,
                   false,
                   SynExpr.LongIdent (_, LongIdentWithDots.LongIdentWithDots (LongIdentOrKeyword s, _), _, _),
                   e2,
                   _) when IsPrefixOperator(DecompileOpName s.Text) -> Some((|OpName|) s, e2)
    | _ -> None

let (|InfixApp|_|) synExpr =
    match synExpr with
    | SynExpr.App (_, true, (Var "::" as e), Tuple ([ e1; e2 ], _), range) -> Some("::", e, e1, e2, range)
    | SynExpr.App (_, _, SynExpr.App (_, true, (Var s as e), e1, _), e2, range) -> Some(s, e, e1, e2, range)
    | _ -> None

let (|NewlineInfixApp|_|) =
    function
    | InfixApp (text, operatorExpr, e1, e2, _) when (newLineInfixOps.Contains(text)) -> Some(text, operatorExpr, e1, e2)
    | _ -> None

let (|NewlineInfixApps|_|) e =
    let rec loop synExpr =
        match synExpr with
        | NewlineInfixApp (s, opE, e, e2) ->
            let e1, es = loop e
            (e1, (s, opE, e2) :: es)
        | e -> (e, [])

    match loop e with
    | e, es when (List.length es > 1) -> Some(e, List.rev es)
    | _ -> None

let (|SameInfixApps|_|) e =
    let rec loop operator synExpr =
        match synExpr with
        | InfixApp (s, opE, e, e2, _) when (s = operator) ->
            let e1, es = loop operator e
            (e1, (s, opE, e2) :: es)
        | e -> (e, [])

    match e with
    | InfixApp (operatorText, _, _, _, _) ->
        match loop operatorText e with
        | e, es when (List.length es > 1) -> Some(e, List.rev es)
        | _ -> None
    | _ -> None

let (|TernaryApp|_|) =
    function
    | SynExpr.App (_, _, SynExpr.App (_, _, SynExpr.App (_, true, Var "?<-", e1, _), e2, _), e3, _) -> Some(e1, e2, e3)
    | _ -> None

let (|MatchLambda|_|) =
    function
    | SynExpr.MatchLambda (_, keywordRange, pats, _, _) -> Some(keywordRange, pats)
    | _ -> None

let (|JoinIn|_|) =
    function
    | SynExpr.JoinIn (e1, _, e2, _) -> Some(e1, e2)
    | _ -> None

/// Unfold a list of let bindings
/// Recursive and use properties have to be determined at this point
let rec (|LetOrUses|_|) =
    function
    | SynExpr.LetOrUse (isRec, isUse, xs, (LetOrUses (ys, e) as body), _, trivia) ->
        let prefix =
            if isUse then "use "
            elif isRec then "let rec "
            else "let "

        let xs' =
            let lastIndex = xs.Length - 1

            List.mapi
                (fun i x ->
                    if i = 0 then
                        (prefix,
                         x,
                         if i = lastIndex then
                             trivia.InKeyword
                         else
                             None)
                    else
                        ("and ",
                         x,
                         if i = lastIndex then
                             trivia.InKeyword
                         else
                             None))
                xs

        Some(xs' @ ys, e)
    | SynExpr.LetOrUse (isRec, isUse, xs, e, _, trivia) ->
        let prefix =
            if isUse then "use "
            elif isRec then "let rec "
            else "let "

        let xs' =
            let lastIndex = xs.Length - 1

            List.mapi
                (fun i x ->
                    if i = 0 then
                        (prefix,
                         x,
                         if i = lastIndex then
                             trivia.InKeyword
                         else
                             None)
                    else
                        ("and ",
                         x,
                         if i = lastIndex then
                             trivia.InKeyword
                         else
                             None))
                xs

        Some(xs', e)
    | _ -> None

type ComputationExpressionStatement =
    | LetOrUseStatement of prefix: string * binding: SynBinding * inKeyword: range option
    | LetOrUseBangStatement of isUse: bool * SynPat * equalsRange: range option * SynExpr * range: range
    | AndBangStatement of SynPat * equalsRange: range * SynExpr * range: range
    | OtherStatement of SynExpr

let rec collectComputationExpressionStatements
    (e: SynExpr)
    (finalContinuation: ComputationExpressionStatement list -> ComputationExpressionStatement list)
    : ComputationExpressionStatement list =
    match e with
    | LetOrUses (bindings, body) ->
        let letBindings = bindings |> List.map LetOrUseStatement

        collectComputationExpressionStatements body (fun bodyStatements ->
            [ yield! letBindings
              yield! bodyStatements ]
            |> finalContinuation)
    | SynExpr.LetOrUseBang (_, isUse, _, pat, equalsRange, expr, andBangs, body, r) ->
        let letOrUseBang = LetOrUseBangStatement(isUse, pat, equalsRange, expr, r)

        let andBangs =
            andBangs
            |> List.map (fun (SynExprAndBang (_, _, _, ap, ae, range, trivia)) ->
                AndBangStatement(ap, trivia.EqualsRange, ae, range))

        collectComputationExpressionStatements body (fun bodyStatements ->
            [ letOrUseBang
              yield! andBangs
              yield! bodyStatements ]
            |> finalContinuation)
    | SynExpr.Sequential (_, _, e1, e2, _) ->
        let continuations: ((ComputationExpressionStatement list -> ComputationExpressionStatement list) -> ComputationExpressionStatement list) list =
            [ collectComputationExpressionStatements e1
              collectComputationExpressionStatements e2 ]

        let finalContinuation (nodes: ComputationExpressionStatement list list) : ComputationExpressionStatement list =
            List.collect id nodes |> finalContinuation

        Continuation.sequence continuations finalContinuation
    | expr -> finalContinuation [ OtherStatement expr ]

/// Matches if the SynExpr has some or of computation expression member call inside.
let rec (|CompExprBody|_|) expr =
    match expr with
    | SynExpr.LetOrUse(body = CompExprBody _)
    | SynExpr.LetOrUseBang _
    | SynExpr.Sequential _ -> Some(collectComputationExpressionStatements expr id)
    | _ -> None

let (|ForEach|_|) =
    function
    | SynExpr.ForEach (_, _, SeqExprOnly true, _, pat, e1, SingleExpr (Yield _, e2), _) -> Some(pat, e1, e2, true)
    | SynExpr.ForEach (_, _, SeqExprOnly isArrow, _, pat, e1, e2, _) -> Some(pat, e1, e2, isArrow)
    | _ -> None

let (|DotIndexedSet|_|) =
    function
    | SynExpr.DotIndexedSet (objectExpr, indexArgs, valueExpr, _, _, _) -> Some(objectExpr, indexArgs, valueExpr)
    | _ -> None

let (|NamedIndexedPropertySet|_|) =
    function
    | SynExpr.NamedIndexedPropertySet (LongIdentWithDots ident, e1, e2, _) -> Some(ident, e1, e2)
    | _ -> None

let (|DotNamedIndexedPropertySet|_|) =
    function
    | SynExpr.DotNamedIndexedPropertySet (e, LongIdentWithDots ident, e1, e2, _) -> Some(e, ident, e1, e2)
    | _ -> None

let (|DotIndexedGet|_|) =
    function
    | SynExpr.DotIndexedGet (objectExpr, indexArgs, _, _) -> Some(objectExpr, indexArgs)
    | _ -> None

let (|DotGet|_|) =
    function
    | SynExpr.DotGet (e, _, LongIdentWithDotsPieces lids, _) -> Some(e, lids)
    | _ -> None

/// Match function call followed by Property
let (|DotGetAppParen|_|) e =
    match e with
    //| App(e, [DotGet (Paren _ as p, (s,r))]) -> Some (e, p, s, r)
    | DotGet (App (e, [ Paren (_, Tuple _, _, _) as px ]), lids) -> Some(e, px, lids)
    | DotGet (App (e, [ Paren (_, singleExpr, _, _) as px ]), lids) ->
        match singleExpr with
        | SynExpr.Lambda _
        | SynExpr.MatchLambda _ -> None
        | _ -> Some(e, px, lids)
    | DotGet (App (e, [ ConstExpr (SynConst.Unit, _) as px ]), lids) -> Some(e, px, lids)
    | _ -> None

let (|DotGetAppDotGetAppParenLambda|_|) (e: SynExpr) =
    match e with
    | DotGet (App (DotGet (App (e, [ Paren (_, SynExpr.Lambda _, _, _) as px ]), appLids), es), lids) ->
        Some(e, px, appLids, es, lids)
    | _ -> None

/// Gather series of application for line breaking
let rec (|DotGetApp|_|) =
    function
    | SynExpr.App (_, _, DotGet (DotGetApp (e, es), s), e', _) -> Some(e, [ yield! es; yield (s, e', None) ])
    | SynExpr.App (_, _, DotGet (e, s), e', _) -> Some(e, [ (s, e', None) ])
    | SynExpr.App (_, _, TypeApp (DotGet (DotGetApp (e, es), s), lt, ts, gt), e', _) ->
        Some(
            e,
            [ yield! es
              yield (s, e', Some(lt, ts, gt)) ]
        )
    | SynExpr.App (_, _, TypeApp (DotGet (e, s), lt, ts, gt), e', _) -> Some(e, [ (s, e', Some(lt, ts, gt)) ])
    | _ -> None

let (|DotSet|_|) =
    function
    | SynExpr.DotSet (e1, LongIdentWithDots s, e2, _) -> Some(e1, s, e2)
    | _ -> None

let (|IfThenElse|_|) =
    function
    | SynExpr.IfThenElse _ as e -> Some e
    | _ -> None

let rec (|ElIf|_|) =
    function
    | SynExpr.IfThenElse (e1,
                          e2,
                          Some (ElIf ((_, eshIfKw, eshIsElif, eshE1, eshThenKw, eshE2) :: es, elseInfo, _)),
                          _,
                          _,
                          range,
                          trivia) ->
        Some(
            ((None, trivia.IfKeyword, trivia.IsElif, e1, trivia.ThenKeyword, e2)
             :: (trivia.ElseKeyword, eshIfKw, eshIsElif, eshE1, eshThenKw, eshE2)
                :: es),
            elseInfo,
            range
        )

    | SynExpr.IfThenElse (e1, e2, e3, _, _, range, trivia) ->
        Some([ (None, trivia.IfKeyword, trivia.IsElif, e1, trivia.ThenKeyword, e2) ], (trivia.ElseKeyword, e3), range)
    | _ -> None

let (|Record|_|) =
    function
    | SynExpr.Record (inheritOpt, eo, xs, StartEndRange 1 (openingBrace, _, closingBrace)) ->
        let inheritOpt =
            inheritOpt
            |> Option.map (fun (typ, expr, _, _, _) -> (typ, expr))

        Some(openingBrace, inheritOpt, xs, Option.map fst eo, closingBrace)
    | _ -> None

let (|AnonRecord|_|) =
    function
    | SynExpr.AnonRecd (isStruct, copyInfo, fields, _) -> Some(isStruct, fields, Option.map fst copyInfo)
    | _ -> None

let (|ObjExpr|_|) =
    function
    | SynExpr.ObjExpr (t, eio, withKeyword, bd, members, ims, _, range) ->
        Some(t, eio, withKeyword, bd, members, ims, range)
    | _ -> None

let (|LongIdentSet|_|) =
    function
    | SynExpr.LongIdentSet (LongIdentWithDots s, e, r) -> Some(s, e, r)
    | _ -> None

let (|TryWith|_|) =
    function
    | SynExpr.TryWith (e, cs, _, _, _, trivia) -> Some(trivia.TryKeyword, e, trivia.WithKeyword, cs)
    | _ -> None

let (|TryFinally|_|) =
    function
    | SynExpr.TryFinally (e1, e2, _, _, _, trivia) -> Some(trivia.TryKeyword, e1, trivia.FinallyKeyword, e2)
    | _ -> None

let (|ParsingError|_|) =
    function
    | SynExpr.ArbitraryAfterError (_, r)
    | SynExpr.FromParseError (_, r)
    | SynExpr.DiscardAfterMissingQualificationAfterDot (_, r) -> Some r
    | _ -> None

let (|ILEmbedded|_|) =
    function
    | SynExpr.LibraryOnlyILAssembly (_, _, _, _, r) -> Some(r)
    | _ -> None

let (|LibraryOnlyStaticOptimization|_|) (e: SynExpr) =
    match e with
    | SynExpr.LibraryOnlyStaticOptimization (constraints, e, optExpr, _) -> Some(optExpr, constraints, e)
    | _ -> None

let (|UnsupportedExpr|_|) =
    function
    // Temporarily ignore these cases not often used outside FSharp.Core
    | SynExpr.LibraryOnlyUnionCaseFieldGet (_, _, _, r)
    | SynExpr.LibraryOnlyUnionCaseFieldSet (_, _, _, _, r) -> Some r
    | _ -> None

// Patterns (18 cases, lacking to handle 2 cases)

let (|PatOptionalVal|_|) =
    function
    | SynPat.OptionalVal (Ident s, _) -> Some s
    | _ -> None

let (|PatAttrib|_|) =
    function
    | SynPat.Attrib (p, ats, _) -> Some(p, ats)
    | _ -> None

let (|PatOr|_|) =
    function
    | SynPat.Or (p1, p2, _, trivia) -> Some(p1, trivia.BarRange, p2)
    | _ -> None

let rec (|PatOrs|_|) =
    function
    | PatOr (PatOrs (p1, pats), barRange, p2) -> Some(p1, [ yield! pats; yield (barRange, p2) ])
    | PatOr (p1, barRange, p2) -> Some(p1, [ barRange, p2 ])
    | _ -> None

let (|PatAnds|_|) =
    function
    | SynPat.Ands (ps, _) -> Some ps
    | _ -> None

type PatNullaryKind =
    | PatNull
    | PatWild

let (|PatNullary|_|) =
    function
    | SynPat.Null _ -> Some PatNull
    | SynPat.Wild _ -> Some PatWild
    | _ -> None

let (|PatTuple|_|) =
    function
    | SynPat.Tuple (false, ps, _) -> Some ps
    | _ -> None

let (|PatStructTuple|_|) =
    function
    | SynPat.Tuple (true, ps, _) -> Some ps
    | _ -> None

type SeqPatKind =
    | PatArray
    | PatList

let (|PatSeq|_|) =
    function
    | SynPat.ArrayOrList (true, ps, _) -> Some(PatArray, ps)
    | SynPat.ArrayOrList (false, ps, _) -> Some(PatList, ps)
    | _ -> None

let (|PatTyped|_|) =
    function
    | SynPat.Typed (p, t, _) -> Some(p, t)
    | _ -> None

let (|PatNamed|_|) pat =
    match pat with
    | SynPat.Named (IdentOrKeyword (OpNameFullInPattern (s, _)), _, ao, _) -> Some(ao, s)
    | _ -> None

let (|PatAs|_|) =
    function
    | SynPat.As (p1, p2, r) -> Some(p1, p2, r)
    | _ -> None

let (|PatLongIdent|_|) =
    function
    | SynPat.LongIdent (LongIdentWithDots.LongIdentWithDots (LongIdentOrKeyword (OpNameFullInPattern (s, _)), _),
                        propertyKeyword,
                        _,
                        tpso,
                        xs,
                        ao,
                        _) ->
        match xs with
        | SynArgPats.Pats ps -> Some(ao, s, propertyKeyword, List.map (fun p -> (None, p)) ps, tpso)
        | SynArgPats.NamePatPairs (nps, _) ->
            Some(
                ao,
                s,
                propertyKeyword,
                List.map (fun (Ident ident, equalsRange, p) -> (Some(ident, equalsRange), p)) nps,
                tpso
            )
    | _ -> None

let (|PatParen|_|) =
    function
    | SynPat.Paren (p, StartEndRange 1 (lpr, _, rpr)) -> Some(lpr, p, rpr)
    | _ -> None

let (|PatRecord|_|) =
    function
    | SynPat.Record (xs, _) -> Some xs
    | _ -> None

let (|PatConst|_|) =
    function
    | SynPat.Const (c, r) -> Some(c, r)
    | _ -> None

let (|PatUnitConst|_|) =
    function
    | SynPat.Const (Unit, _) -> Some()
    | _ -> None

let (|PatIsInst|_|) =
    function
    | SynPat.IsInst (t, _) -> Some t
    | _ -> None

let (|PatQuoteExpr|_|) =
    function
    | SynPat.QuoteExpr (e, _) -> Some e
    | _ -> None

let (|PatExplicitCtor|_|) =
    function
    | SynPat.LongIdent (LongIdentWithDots.LongIdentWithDots ([ newIdent ], _),
                        _propertyKeyword,
                        _,
                        _,
                        SynArgPats.Pats [ PatParen _ as pat ],
                        ao,
                        _) when (newIdent.idText = "new") -> Some(ao, pat)
    | _ -> None

// Members
type SynSimplePats with
    member pat.Range =
        match pat with
        | SynSimplePats.SimplePats (_, r)
        | SynSimplePats.Typed (_, _, r) -> r


let (|SPAttrib|SPId|SPTyped|) =
    function
    | SynSimplePat.Attrib (sp, ats, _) -> SPAttrib(ats, sp)
    // Not sure compiler generated SPIds are used elsewhere.
    | SynSimplePat.Id (Ident s, _, isGen, _, isOptArg, _) -> SPId(s, isOptArg, isGen)
    | SynSimplePat.Typed (sp, t, _) -> SPTyped(sp, t)

let (|SimplePats|SPSTyped|) =
    function
    | SynSimplePats.SimplePats (ps, _) -> SimplePats ps
    | SynSimplePats.Typed (ps, t, _) -> SPSTyped(ps, t)

let (|RecordField|) =
    function
    | SynField (ats, _, ido, _, _, px, ao, _) -> (ats, px, ao, Option.map (|Ident|) ido)

let (|Clause|) (SynMatchClause (p, eo, e, _, _, trivia)) = (p, eo, trivia.ArrowRange, e)

/// Process compiler-generated matches in an appropriate way
let rec private skipGeneratedLambdas expr =
    match expr with
    | SynExpr.Lambda (inLambdaSeq = true; body = bodyExpr) -> skipGeneratedLambdas bodyExpr
    | _ -> expr

and skipGeneratedMatch expr =
    match expr with
    | SynExpr.Match (_matchKeyword,
                     _,
                     _,
                     _,
                     [ SynMatchClause.SynMatchClause (resultExpr = innerExpr) as clause ],
                     matchRange) when matchRange.Start = clause.Range.Start -> skipGeneratedMatch innerExpr
    | _ -> expr

let (|Lambda|_|) =
    function
    | SynExpr.Lambda (_, _, _, _, Some (pats, body), range, trivia) ->
        let inline getLambdaBodyExpr expr =
            let skippedLambdas = skipGeneratedLambdas expr
            skipGeneratedMatch skippedLambdas

        Some(pats, trivia.ArrowRange, getLambdaBodyExpr body, range)
    | _ -> None

let (|AppWithLambda|_|) (e: SynExpr) =
    match e with
    | App (e, es) ->
        let rec visit (es: SynExpr list) (finalContinuation: SynExpr list -> SynExpr list) =
            match es with
            | [] -> None
            | [ Paren (lpr, Lambda (pats, arrowRange, body, range), rpr, pr) ] ->
                Some(e, finalContinuation [], lpr, (Choice1Of2(pats, arrowRange, body, range)), rpr, pr)
            | [ Paren (lpr, (MatchLambda (keywordRange, pats) as me), rpr, pr) ] ->
                Some(e, finalContinuation [], lpr, (Choice2Of2(keywordRange, pats, me.Range)), rpr, pr)
            | h :: tail ->
                match h with
                | Paren (_, Lambda _, _, _)
                | Paren (_, MatchLambda _, _, _) -> None
                | _ -> visit tail (fun leadingArguments -> h :: leadingArguments |> finalContinuation)

        visit es id
    | _ -> None

// Type definitions

let (|TDSREnum|TDSRUnion|TDSRRecord|TDSRNone|TDSRTypeAbbrev|TDSRException|) =
    function
    | SynTypeDefnSimpleRepr.Enum (ecs, _) -> TDSREnum ecs
    | SynTypeDefnSimpleRepr.Union (ao, xs, _) -> TDSRUnion(ao, xs)
    | SynTypeDefnSimpleRepr.Record (ao, fs, StartEndRange 1 (openingBrace, _, closingBrace)) ->
        TDSRRecord(openingBrace, ao, fs, closingBrace)
    | SynTypeDefnSimpleRepr.None _ -> TDSRNone()
    | SynTypeDefnSimpleRepr.TypeAbbrev (_, t, _) -> TDSRTypeAbbrev t
    | SynTypeDefnSimpleRepr.General _ -> failwith "General should not appear in the parse tree"
    | SynTypeDefnSimpleRepr.LibraryOnlyILAssembly _ -> failwith "LibraryOnlyILAssembly is not supported yet"
    | SynTypeDefnSimpleRepr.Exception repr -> TDSRException repr

let (|Simple|ObjectModel|ExceptionRepr|) =
    function
    | SynTypeDefnRepr.Simple (tdsr, _) -> Simple tdsr
    | SynTypeDefnRepr.ObjectModel (tdk, mds, range) -> ObjectModel(tdk, mds, range)
    | SynTypeDefnRepr.Exception repr -> ExceptionRepr repr

let (|MemberDefnList|) mds =
    // Assume that there is at most one implicit constructor
    let impCtor =
        List.tryFind
            (function
            | MDImplicitCtor _ -> true
            | _ -> false)
            mds
    // Might need to sort so that let and do bindings come first
    let others =
        List.filter
            (function
            | MDImplicitCtor _ -> false
            | _ -> true)
            mds

    (impCtor, others)

let (|SigSimple|SigObjectModel|SigExceptionRepr|) =
    function
    | SynTypeDefnSigRepr.Simple (tdsr, _) -> SigSimple tdsr
    | SynTypeDefnSigRepr.ObjectModel (tdk, mds, _) -> SigObjectModel(tdk, mds)
    | SynTypeDefnSigRepr.Exception repr -> SigExceptionRepr repr

type TypeDefnKindSingle =
    | TCUnspecified
    | TCClass
    | TCInterface
    | TCStruct
    | TCRecord
    | TCUnion
    | TCAbbrev
    | TCOpaque
    | TCAugmentation of withKeyword: range
    | TCIL

let (|TCSimple|TCDelegate|) =
    function
    | SynTypeDefnKind.Unspecified -> TCSimple TCUnspecified
    | SynTypeDefnKind.Class -> TCSimple TCClass
    | SynTypeDefnKind.Interface -> TCSimple TCInterface
    | SynTypeDefnKind.Struct -> TCSimple TCStruct
    | SynTypeDefnKind.Record -> TCSimple TCRecord
    | SynTypeDefnKind.Union -> TCSimple TCUnion
    | SynTypeDefnKind.Abbrev -> TCSimple TCAbbrev
    | SynTypeDefnKind.Opaque -> TCSimple TCOpaque
    | SynTypeDefnKind.Augmentation withKeyword -> TCSimple(TCAugmentation withKeyword)
    | SynTypeDefnKind.IL -> TCSimple TCIL
    | SynTypeDefnKind.Delegate (t, vi) -> TCDelegate(t, vi)

let (|TypeDef|)
    (SynTypeDefn (SynComponentInfo (ats, tds, tcs, LongIdent s, px, preferPostfix, ao, _), tdr, ms, _, _, trivia))
    =
    (ats, px, trivia.TypeKeyword, ao, tds, tcs, trivia.EqualsRange, tdr, trivia.WithKeyword, ms, s, preferPostfix)

let (|SigTypeDef|)
    (SynTypeDefnSig (SynComponentInfo (ats, tds, tcs, LongIdent s, px, preferPostfix, ao, _),
                     equalsRange,
                     tdr,
                     withKeyword,
                     ms,
                     range))
    =
    (ats, px, ao, tds, tcs, equalsRange, tdr, withKeyword, ms, s, preferPostfix, range)

let (|TyparDecl|) (SynTyparDecl (ats, tp)) = (ats, tp)

// Types (15 cases)

let (|THashConstraint|_|) =
    function
    | SynType.HashConstraint (t, _) -> Some t
    | _ -> None

let (|TMeasurePower|_|) =
    function
    | SynType.MeasurePower (t, RationalConst n, _) -> Some(t, n)
    | _ -> None

let (|TMeasureDivide|_|) =
    function
    | SynType.MeasureDivide (t1, t2, _) -> Some(t1, t2)
    | _ -> None

let (|TStaticConstant|_|) =
    function
    | SynType.StaticConstant (c, r) -> Some(c, r)
    | _ -> None

let (|TStaticConstantExpr|_|) =
    function
    | SynType.StaticConstantExpr (c, _) -> Some c
    | _ -> None

let (|TStaticConstantNamed|_|) =
    function
    | SynType.StaticConstantNamed (t1, t2, _) -> Some(t1, t2)
    | _ -> None

let (|TArray|_|) =
    function
    | SynType.Array (n, t, r) -> Some(t, n, r)
    | _ -> None

let (|TAnon|_|) =
    function
    | SynType.Anon _ -> Some()
    | _ -> None

let (|TVar|_|) =
    function
    | SynType.Var (tp, r) -> Some(tp, r)
    | _ -> None

let (|TFun|_|) =
    function
    | SynType.Fun (t1, t2, _) -> Some(t1, t2)
    | _ -> None

// Arrow type is right-associative
let rec (|TFuns|_|) =
    function
    | TFun (t1, TFuns ts) -> Some [ yield t1; yield! ts ]
    | TFun (t1, t2) -> Some [ t1; t2 ]
    | _ -> None

let (|TApp|_|) =
    function
    | SynType.App (t, lessRange, ts, _, greaterRange, isPostfix, range) ->
        Some(t, lessRange, ts, greaterRange, isPostfix, range)
    | _ -> None

let (|TLongIdentApp|_|) =
    function
    | SynType.LongIdentApp (t, LongIdentWithDots s, lessRange, ts, _, greaterRange, _) ->
        Some(t, s, lessRange, ts, greaterRange)
    | _ -> None

let (|TTuple|_|) =
    function
    | SynType.Tuple (false, ts, _) -> Some ts
    | _ -> None

let (|TStructTuple|_|) =
    function
    | SynType.Tuple (true, ts, _) -> Some ts
    | _ -> None

let (|TWithGlobalConstraints|_|) =
    function
    | SynType.WithGlobalConstraints (t, tcs, _) -> Some(t, tcs)
    | _ -> None

let (|TLongIdent|_|) =
    function
    | SynType.LongIdent (LongIdentWithDots s) -> Some s
    | _ -> None

let (|TAnonRecord|_|) =
    function
    | SynType.AnonRecd (isStruct, fields, _) -> Some(isStruct, fields)
    | _ -> None

let (|TParen|_|) =
    function
    | SynType.Paren (innerType, StartEndRange 1 (lpr, _, rpr)) -> Some(lpr, innerType, rpr)
    | _ -> None
// Type parameter

type SingleTyparConstraintKind =
    | TyparIsValueType
    | TyparIsReferenceType
    | TyparIsUnmanaged
    | TyparSupportsNull
    | TyparIsComparable
    | TyparIsEquatable
    override x.ToString() =
        match x with
        | TyparIsValueType -> "struct"
        | TyparIsReferenceType -> "not struct"
        | TyparIsUnmanaged -> "unmanaged"
        | TyparSupportsNull -> "null"
        | TyparIsComparable -> "comparison"
        | TyparIsEquatable -> "equality"

let (|TyparSingle|TyparDefaultsToType|TyparSubtypeOfType|TyparSupportsMember|TyparIsEnum|TyparIsDelegate|) =
    function
    | SynTypeConstraint.WhereTyparIsValueType (tp, _) -> TyparSingle(TyparIsValueType, tp)
    | SynTypeConstraint.WhereTyparIsReferenceType (tp, _) -> TyparSingle(TyparIsReferenceType, tp)
    | SynTypeConstraint.WhereTyparIsUnmanaged (tp, _) -> TyparSingle(TyparIsUnmanaged, tp)
    | SynTypeConstraint.WhereTyparSupportsNull (tp, _) -> TyparSingle(TyparSupportsNull, tp)
    | SynTypeConstraint.WhereTyparIsComparable (tp, _) -> TyparSingle(TyparIsComparable, tp)
    | SynTypeConstraint.WhereTyparIsEquatable (tp, _) -> TyparSingle(TyparIsEquatable, tp)
    | SynTypeConstraint.WhereTyparDefaultsToType (tp, t, _) -> TyparDefaultsToType(tp, t)
    | SynTypeConstraint.WhereTyparSubtypeOfType (tp, t, _) -> TyparSubtypeOfType(tp, t)
    | SynTypeConstraint.WhereTyparSupportsMember (tps, msg, _) ->
        TyparSupportsMember(
            List.choose
                (function
                | SynType.Var (tp, _) -> Some tp
                | _ -> None)
                tps,
            msg
        )
    | SynTypeConstraint.WhereTyparIsEnum (tp, ts, _) -> TyparIsEnum(tp, ts)
    | SynTypeConstraint.WhereTyparIsDelegate (tp, ts, _) -> TyparIsDelegate(tp, ts)

let (|MSMember|MSInterface|MSInherit|MSValField|MSNestedType|) =
    function
    | SynMemberSig.Member (vs, mf, _) -> MSMember(vs, mf)
    | SynMemberSig.Interface (t, _) -> MSInterface t
    | SynMemberSig.Inherit (t, _) -> MSInherit t
    | SynMemberSig.ValField (f, _) -> MSValField f
    | SynMemberSig.NestedType (tds, _) -> MSNestedType tds

let (|Val|)
    (SynValSig (ats,
                (IdentOrKeyword (OpNameFullInPattern (s, _)) as ident),
                SynValTyparDecls (typars, _),
                t,
                vi,
                isInline,
                isMutable,
                px,
                ao,
                eo,
                _,
                range))
    =
    (ats, px, ao, s, ident.idRange, t, vi, isInline, isMutable, typars, eo, range)

// Misc

let (|RecordFieldName|) ((LongIdentWithDots s, _): RecordFieldName, eo: SynExpr option, _) = (s, eo)

let (|AnonRecordFieldName|) (ident: Ident, eq: range option, e: SynExpr) = (ident.idText, ident.idRange, eq, e)
let (|AnonRecordFieldType|) (Ident s: Ident, t: SynType) = (s, t)

let (|PatRecordFieldName|) ((LongIdent s1, Ident s2), _, p) = (s1, s2, p)

let (|ValInfo|) (SynValInfo (aiss, ai)) = (aiss, ai)

let (|ArgInfo|) (SynArgInfo (attribs, isOpt, ido)) =
    (attribs, Option.map (|Ident|) ido, isOpt)

/// Extract function arguments with their associated info
let (|FunType|) (t, ValInfo (argTypes, returnType)) =
    // Parse arg info by attach them into relevant types.
    // The number of arg info will determine semantics of argument types.
    let rec loop =
        function
        | TFun (t1, t2), argType :: argTypes -> (t1, argType) :: loop (t2, argTypes)
        | t, [] -> [ (t, [ returnType ]) ]
        | _ -> []

    loop (t, argTypes)

/// A rudimentary recognizer for extern functions
/// Probably we should use lexing information to improve its accuracy
let (|Extern|_|) =
    function
    | Let (LetBinding (ats,
                       px,
                       _,
                       ao,
                       _,
                       _,
                       PatLongIdent (_, s, _, [ _, PatTuple ps ], _),
                       _,
                       TypedExpr (Typed, _, t),
                       _)) ->
        let hasDllImportAttr =
            ats
            |> List.exists (fun { Attributes = attrs } ->
                attrs
                |> List.exists (fun (Attribute (name, _, _)) -> name.EndsWith("DllImport")))

        if hasDllImportAttr then
            Some(ats, px, ao, t, s, ps)
        else
            None
    | _ -> None

let rec (|UppercaseSynExpr|LowercaseSynExpr|) (synExpr: SynExpr) =
    let upperOrLower (v: string) =
        let isUpper =
            Seq.tryHead v
            |> Option.map Char.IsUpper
            |> Option.defaultValue false

        if isUpper then
            UppercaseSynExpr
        else
            LowercaseSynExpr

    match synExpr with
    | SynExpr.Ident (Ident id) -> upperOrLower id

    | SynExpr.LongIdent (_, LongIdentWithDots lid, _, _) ->
        let lastPart = Array.tryLast (lid.Split('.'))

        match lastPart with
        | Some lp -> upperOrLower lp
        | None -> LowercaseSynExpr

    | SynExpr.DotGet (_, _, LongIdentWithDots lid, _) -> upperOrLower lid

    | SynExpr.DotIndexedGet (expr, _, _, _)
    | SynExpr.TypeApp (expr, _, _, _, _, _, _) -> (|UppercaseSynExpr|LowercaseSynExpr|) expr
    | _ -> failwithf "cannot determine if synExpr %A is uppercase or lowercase" synExpr

let rec (|UppercaseSynType|LowercaseSynType|) (synType: SynType) =
    let upperOrLower (v: string) =
        let isUpper =
            Seq.tryHead v
            |> Option.map Char.IsUpper
            |> Option.defaultValue false

        if isUpper then
            UppercaseSynType
        else
            LowercaseSynType

    match synType with
    | SynType.LongIdent (LongIdentWithDots lid) -> lid.Split('.') |> Seq.last |> upperOrLower
    | SynType.Var (Typar (s, _, _), _) -> upperOrLower s
    | SynType.App (st, _, _, _, _, _, _) -> (|UppercaseSynType|LowercaseSynType|) st
    | _ -> failwithf "cannot determine if synType %A is uppercase or lowercase" synType

let (|IndexWithoutDotExpr|ElmishReactWithoutChildren|ElmishReactWithChildren|NonAppExpr|) e =
    match e with
    | SynExpr.App (ExprAtomicFlag.Atomic, false, identifierExpr, SynExpr.ArrayOrListComputed (false, indexExpr, _), _) ->
        IndexWithoutDotExpr(identifierExpr, indexExpr)
    | SynExpr.App (ExprAtomicFlag.NonAtomic,
                   false,
                   identifierExpr,
                   (SynExpr.ArrayOrListComputed (isArray = false; expr = indexExpr) as argExpr),
                   _) when (RangeHelpers.isAdjacentTo identifierExpr.Range argExpr.Range) ->
        IndexWithoutDotExpr(identifierExpr, indexExpr)
    | SynExpr.App (_, false, OptVar (ident, _, _), ArrayOrList (sr, isArray, children, er, _), _) ->
        ElmishReactWithoutChildren(ident, sr, isArray, children, er)
    | SynExpr.App (_,
                   false,
                   SynExpr.App (_, false, OptVar ident, (ArrayOrList _ as attributes), _),
                   ArrayOrList (sr, isArray, children, er, r),
                   _) -> ElmishReactWithChildren(ident, attributes, (isArray, sr, children, er))
    | _ -> NonAppExpr

let isIfThenElseWithYieldReturn e =
    match e with
    | SynExpr.IfThenElse (thenExpr = SynExpr.YieldOrReturn _; elseExpr = None)
    | SynExpr.IfThenElse (thenExpr = SynExpr.YieldOrReturn _; elseExpr = Some (SynExpr.YieldOrReturn _))
    | SynExpr.IfThenElse (thenExpr = SynExpr.YieldOrReturnFrom _; elseExpr = None)
    | SynExpr.IfThenElse (thenExpr = SynExpr.YieldOrReturn _; elseExpr = Some (SynExpr.YieldOrReturnFrom _)) -> true
    | _ -> false

let isSynExprLambdaOrIfThenElse =
    function
    | SynExpr.Lambda _
    | SynExpr.IfThenElse _ -> true
    | _ -> false

let (|AppParenTupleArg|_|) e =
    match e with
    | AppSingleParenArg (a, Paren (lpr, Tuple (ts, tr), rpr, pr)) -> Some(a, lpr, ts, tr, rpr, pr)
    | _ -> None

let (|AppParenSingleArg|_|) e =
    match e with
    | AppSingleParenArg (a, Paren (lpr, p, rpr, pr)) -> Some(a, lpr, p, rpr, pr)
    | _ -> None

let (|AppParenArg|_|) e =
    match e with
    | AppParenTupleArg t -> Choice1Of2 t |> Some
    | AppParenSingleArg s -> Choice2Of2 s |> Some
    | _ -> None

let private shouldNotIndentBranch e es =
    let isShortIfBranch e =
        match e with
        | SimpleExpr _
        | Sequential (_, _, true)
        | App _
        | Tuple _
        | Paren (_, Tuple _, _, _) -> true
        | _ -> false

    let isLongElseBranch e =
        match e with
        | LetOrUses _
        | Sequential _
        | Match _
        | TryWith _
        | App (_, [ ObjExpr _ ])
        | NewlineInfixApp (_, _, AppParenTupleArg _, _)
        | NewlineInfixApp (_, _, _, App (_, [ Paren (_, Lambda _, _, _) ])) -> true
        | _ -> false

    List.forall isShortIfBranch es
    && isLongElseBranch e

let (|KeepIndentMatch|_|) (e: SynExpr) =
    let mapClauses matchKeyword matchExpr withKeyword clauses range t =
        match clauses with
        | [] -> None
        | [ (Clause (_, _, _, lastClause)) ] ->
            if shouldNotIndentBranch lastClause [] then
                Some(matchKeyword, matchExpr, withKeyword, clauses, range, t)
            else
                None
        | clauses ->
            let firstClauses =
                clauses
                |> List.take (clauses.Length - 1)
                |> List.map (fun (Clause (_, _, _, expr)) -> expr)

            let (Clause (_, _, _, lastClause)) = List.last clauses

            if shouldNotIndentBranch lastClause firstClauses then
                Some(matchKeyword, matchExpr, withKeyword, clauses, range, t)
            else
                None

    match e with
    | Match (matchKeyword, matchExpr, withKeyword, clauses) ->
        mapClauses matchKeyword matchExpr withKeyword clauses e.Range SynExpr_Match
    | MatchBang (matchKeyword, matchExpr, withKeyword, clauses) ->
        mapClauses matchKeyword matchExpr withKeyword clauses e.Range SynExpr_MatchBang
    | _ -> None

let (|KeepIndentIfThenElse|_|) (e: SynExpr) =
    match e with
    | ElIf (branches, (_, Some elseExpr), _) ->
        let branchBodies = branches |> List.map (fun (_, _, _, e, _, _) -> e)

        if shouldNotIndentBranch elseExpr branchBodies then
            Some(branches, elseExpr, e.Range)
        else
            None
    | _ -> None
