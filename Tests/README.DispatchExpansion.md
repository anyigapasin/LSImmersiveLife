These checks validate sources, catalogs and audio resolution. They do not run GTA natives.

1. Run `python Tests/validate_dispatch_expansion.py` from an extracted Git checkout.
2. Build `LSImmersiveLife.csproj` in Release/x64 using the installation's own
   ScriptHookVDotNet3, LemonUI.SHVDN3 and NAudio references. Override
   `GTAVEnhancedRoot` to a nonexistent directory for validation builds.
3. `DispatchExpansionCatalogChecks.cs` is a separate .NET 8 console harness.
   Compile it as a console application, then pass: repository root, built
   LSImmersiveLife.dll path, and a directory containing the three dependency
   DLLs. It parses every event using the production C# reader, samples the
   production suspect-count chooser, rejects an invalid action, and exercises
   the production audio resolver/deduplication without opening an audio device.
4. `CloudValidation.proj` imports the original production project and supplies
   external references for cross-platform source validation. Pass
   `/p:CloudReferenceRoot=<directory>` containing
   `net48/build/.NETFramework/v4.8` from Microsoft's net48 reference package,
   and `references` containing the three dependency DLLs. It redirects the
   game target to `obj/NoGame`; no dependencies are bundled with this source.

The Python check reports existing missing WAVs separately. New references must
resolve; existing missing recordings are not fabricated or treated as playable.
