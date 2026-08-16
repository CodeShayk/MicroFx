; Analyzer rules added since the last shipped release.
; Release tracking makes a new or changed rule a reviewed diff rather than a surprise build break
; for every consumer who takes the next version.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------------
MFX1001 | MicroFx.Composition | Error | Feature id uses the reserved 'microfx.' prefix
MFX1003 | MicroFx.Composition | Warning | Blocking call in a feature's Configure method
MFX1010 | MicroFx.Correctness | Warning | HttpClient constructed directly
MFX1011 | MicroFx.Correctness | Warning | Ambient clock used instead of TimeProvider
MFX1022 | MicroFx.Correctness | Error | Domain event published to a transport
MFX2001 | MicroFx.Platform | Error | Built-in feature must register services with TryAdd
