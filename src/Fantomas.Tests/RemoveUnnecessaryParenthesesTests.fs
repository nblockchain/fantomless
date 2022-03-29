module Fantomas.Tests.RemoveUnnecessaryParenthesesTests

open NUnit.Framework
open FsUnit
open Fantomas.Tests.TestHelper

[<Test>]
let ``parentheses around single identifiers in if expressions are unnecessary, 684`` () =
    formatSourceString
        false
        """
if (foo) then bar else baz
"""
        config
    |> prepend newline
    |> should
        equal
        """
if foo then bar else baz
"""

[<Test>]
let ``parentheses around single identifiers in if expressions are unnecessary, 684 (multiline)`` () =
    formatSourceString
        false
        """
if (foooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooo) then
    bar else baz
"""
        config
    |> prepend newline
    |> should
        equal
        """
if foooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooo then
    bar
else
    baz
"""

[<Test>]
let ``parentheses around single identifiers in elif expressions are unnecessary, 684`` () =
    formatSourceString
        false
        """
if foo then bar
elif (baz) then foobar
"""
        config
    |> prepend newline
    |> should
        equal
        """
if foo then bar
elif baz then foobar
"""

[<Test>]
let ``parentheses shouldn't be removed (1)`` () =
    formatSourceString
        false
        """
if foo then (bar)
elif baz then foobar
"""
        config
    |> prepend newline
    |> should
        equal
        """
if foo then (bar)
elif baz then foobar
"""

[<Test>]
let ``parentheses shouldn't be removed (2)`` () =
    formatSourceString
        false
        """
if foo then bar
elif baz then (foobar)
"""
        config
    |> prepend newline
    |> should
        equal
        """
if foo then bar
elif baz then (foobar)
"""

[<Test>]
let ``parentheses around single identifiers in elif expressions are unnecessary, 684 (multiline)`` () =
    formatSourceString
        false
        """
if fooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooo then bar
elif (bazzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz) then foobar
"""
        config
    |> prepend newline
    |> should
        equal
        """
if fooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooooo then
    bar
elif bazzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz then
    foobar
"""

[<Test>]
let ``parentheses in discriminated unions are unnecessary, 684`` () =
    formatSourceString
        false
        """
match foo with
| None -> ()
| Some(bar) -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match foo with
| None -> ()
| Some bar -> ()
"""

[<Test>]
let ``parentheses in discriminated unions are unnecessary (2), 684`` () =
    formatSourceString
        false
        """
match foo with
| Something -> ()
| OtherThing(bar) -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match foo with
| Something -> ()
| OtherThing bar -> ()
"""

[<Test>]
let ``parentheses in discriminated unions are unnecessary (3), 684`` () =
    formatSourceString
        false
        """
match foo with
| Something -> ()
| OtherThing(bar), baz -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match foo with
| Something -> ()
| OtherThing bar, baz -> ()
"""

[<Test>]
let ``parentheses in discriminated unions are unnecessary (4), 684`` () =
    formatSourceString
        false
        """
match baz with
| Something -> ()
| OtherThing(_) -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match baz with
| Something -> ()
| OtherThing _ -> ()
"""

[<Test>]
let ``parentheses in discriminated unions are unnecessary (5), 684`` () =
    formatSourceString
        false
        """
match baz with
| Something -> ()
| (_) -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match baz with
| Something -> ()
| _ -> ()
"""

[<Test>]
let ``parentheses in discriminated unions should be kept (I), 684`` () =
    formatSourceString
        false
        """
match foo with
| Something -> ()
| OtherThing (bar, baz) -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match foo with
| Something -> ()
| OtherThing (bar, baz) -> ()
"""

[<Test>]
let ``parentheses in discriminated unions should be kept (II), 684`` () =
    formatSourceString
        false
        """
match foo with
| Something -> ()
| OtherThing (AndLastThing bar) -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match foo with
| Something -> ()
| OtherThing (AndLastThing bar) -> ()
"""

[<Test>]
let ``parentheses in discriminated unions should be kept (III), 684`` () =
    formatSourceString
        false
        """
match foo with
| Something -> ()
| OtherThing ({ Bar = Baz.FooBar } as fooBarBaz) -> ()
"""
        config
    |> prepend newline
    |> should
        equal
        """
match foo with
| Something -> ()
| OtherThing ({ Bar = Baz.FooBar } as fooBarBaz) -> ()
"""

[<Test>]
let ``parentheses around lambda's parameters should be removed (I), 684`` () =
    formatSourceString
        false
        """
(fun (foo) -> bar foo) |> ignore
"""
        config
    |> prepend newline
    |> should
        equal
        """
(fun foo -> bar foo) |> ignore
"""

[<Test>]
let ``parentheses around function parameters should be removed (II), 684`` () =
    formatSourceString
        false
        """
(fun foo (bar) -> baz foo) |> ignore
"""
        config
    |> prepend newline
    |> should
        equal
        """
(fun foo bar -> baz foo) |> ignore
"""

[<Test>]
let ``parentheses around lambda's parameters should be kept (I), 684`` () =
    formatSourceString
        false
        """
(fun (foo: bar) -> baz foo) |> ignore
"""
        config
    |> prepend newline
    |> should
        equal
        """
(fun (foo: bar) -> baz foo) |> ignore
"""

[<Test>]
let ``parentheses around lambda's parameters should be kept (II), 684`` () =
    formatSourceString
        false
        """
(fun (foo, bar) -> baz foo, baz bar) |> ignore
"""
        config
    |> prepend newline
    |> should
        equal
        """
(fun (foo, bar) -> baz foo, baz bar) |> ignore
"""

[<Test>]
let ``parentheses around lambda's parameters should be kept (III), 684`` () =
    formatSourceString
        false
        """
(fun (foo: bar) (baz: foobar) -> foobarbaz foo baz) |> ignore
"""
        config
    |> prepend newline
    |> should
        equal
        """
(fun (foo: bar) (baz: foobar) -> foobarbaz foo baz)
|> ignore
"""

[<Test>]
let ``parentheses in untyped params should be removed, 684`` () =
    formatSourceString
        false
        """
module Foo =
    let sum (a) (b) = a + b
"""
        config
    |> prepend newline
    |> should
        equal
        """
module Foo =
    let sum a b = a + b
"""

[<Test>]
let ``parentheses in typed params should be kept, 684`` () =
    formatSourceString
        false
        """
module Foo =
    let sum (a: int) (b: int) = a + b
"""
        config
    |> prepend newline
    |> should
        equal
        """
module Foo =
    let sum (a: int) (b: int) = a + b
"""


[<Test>]
let ``parentheses in function call should be removed`` () =
    formatSourceString
        false
        """
raise(InvalidPassword)
"""
        config
    |> prepend newline
    |> should
        equal
        """
raise InvalidPassword
"""

[<Test>]
let ``nothing should be changed`` () =
    formatSourceString
        false
        """
raise InvalidPassword
"""
        config
    |> prepend newline
    |> should
        equal
        """
raise InvalidPassword
"""

[<Test>]
let ``parentheses should be removed and left pipe introduced between arguments and function call`` () =
    formatSourceString
        false
        """
raise(AddressWithInvalidChecksum None)
"""
        config
    |> prepend newline
    |> should
        equal
        """
raise <| AddressWithInvalidChecksum None
"""

[<Test>]
let ``parentheses should be removed and left pipe introduced between argument(inner function call)  and function call``
    ()
    =
    formatSourceString
        false
        """
raise(Exception("foo"))
"""
        config
    |> prepend newline
    |> should
        equal
        """
raise <| Exception("foo")
"""

[<Test>]
let ``parentheses should be removed and left pipe introduced between argument(inner function call with two arguments)  and function call``
    ()
    =
    formatSourceString
        false
        """
raise(Exception(ex.ToString(), (ex.InnerException)))
"""
        config
    |> prepend newline
    |> should
        equal
        """
raise <| Exception(ex.ToString(), (ex.InnerException))
"""
