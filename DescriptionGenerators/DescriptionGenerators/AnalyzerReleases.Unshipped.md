; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
DESCGEN001 | DescriptionGenerators | Warning | JsonItem key differs from the generated read key (member would be silently empty)
DESCGEN002 | DescriptionGenerators | Warning | Description member is not read by the generated constructor (stays null/default)
DESCGEN003 | DescriptionGenerators | Error | Duplicate description key for an interface (registration would throw at startup)
