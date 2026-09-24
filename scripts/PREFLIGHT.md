# Preflight checks

`scripts/preflight.py` is a static analyser that runs before every
CI build. It catches a class of bug that `dotnet build` cannot:
broken XAML resource references that compile clean and crash on
first paint with a `XamlParseException`.

## What it catches

The script runs five rules over the source tree:

1. **XAML `<StaticResource>` references**. Every `{StaticResource X}`
   in a `.xaml` file must resolve to a key defined in one of the
   loaded `ResourceDictionary` files. A typo (`{StaticResource
   Positve}` instead of `Positive`) compiles clean and crashes on
   first paint.
2. **Dynamic-resource key parity**. A `DynamicResource` in a control
   template must also resolve to a defined key, even though it
   resolves at runtime.
3. **WPF namespace usage**. `<Setter Property="..."/>` in a style
   must use a property that exists on the targeted type. WPF
   silently no-ops an unknown setter, so a typo in a property name
   in `Dark.xaml` would not show up until someone enabled dark
   mode.
4. **Non-ASCII in C#**. C# source files may only contain ASCII
   characters. The Windows `msbuild` runner silently changes the
   source encoding under non-ASCII, which corrupts comments and
   string literals in `.resx` files. (The XAML files use XML
   entities for non-ASCII, which is fine.)
5. **Resource file (`.resx`) well-formedness**. A malformed `.resx`
   file is silently accepted by `dotnet build` and only fails at
   runtime when the resource is loaded.

## When to run

The CI workflow (`.github/workflows/build.yml`) runs the script
before `dotnet build` on the `windows-latest` runner. A failure
aborts the build before any compile time is spent.

Run it locally before any PR that touches XAML:

```powershell
python scripts/preflight.py
```

## Platform requirements

The script runs on **any** platform that has Python 3.8+ -- the
XML parser it uses (`xml.dom.minidom`) is part of the standard
library. There is no Windows-only API surface; the script is
tested on `windows-latest` in CI but works on Linux and macOS
too.

The script does *not* invoke `dotnet` or any WPF API; a CI runner
that does not have .NET installed can still run the preflight
step.

## Limitations

- It does not check that the *value* of a `StaticResource` is
  compatible with the *target* type. A `{StaticResource Primary}`
  in a `Foreground` setter is type-checked at runtime by WPF; a
  string resource bound to a `Brush` setter would still pass
  preflight.
- It does not follow code-behind or compiled bindings. A
  `x:Name` reference that is not actually used in the
  code-behind file is not flagged -- the compiler's `unused
  field` warning is the right tool for that.
- It does not check `Themes/Dark.xaml` against
  `Themes/Light.xaml`. The audit noted that the production
  `ThemeTests` covers key parity; that is a separate xUnit test,
  not a preflight rule.

## Adding a new rule

Each rule is a function returning a list of error strings. The
`main()` function calls every rule and prints the union. To add
a new rule, write a function and add it to the `rules` list in
`main()`. Keep the rule cheap (under 1 second) so the CI step
does not add noticeable latency.
