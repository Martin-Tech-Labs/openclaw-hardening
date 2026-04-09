# FuseSharp runtime note

Testing showed that FuseSharp builds, but the produced assembly is not usable with the current .NET 10 host on this machine for the test app.

Observed facts:
- `FusePidProbe` builds successfully.
- `FuseSharp.dll` is present in output.
- Running the app fails with `FileNotFoundException: Could not load file or assembly 'FuseSharp'`.
- `file FuseSharp.dll` reports:
  - `PE32+ executable (DLL) (console) x86-64 Mono/.Net assembly, for MS Windows`

This suggests the generated FuseSharp artifact / target model is not a straightforward .NET Core/macOS runtime-consumable assembly in this environment.

Possible next directions:
- try a different FUSE .NET library targeting modern .NET/macOS
- build a native interop shim instead of depending on FuseSharp packaging
- use Mono if FuseSharp expects Mono runtime semantics
