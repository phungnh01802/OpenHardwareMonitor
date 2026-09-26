# Bug Report #1 — App builds but crashes immediately on startup

- **Project:** OpenHardwareMonitor (HardwareMonitor/OpenHardwareMonitor)
- **Commit:** `5c3e0f90` ("bump nuget packahes")
- **OS:** Windows 11 (build 10.0.26100/26200)
- **Toolchain:** Visual Studio 2022/2026 Community, .NET Framework 4.7.2 target
- **Severity:** Blocker — the application cannot be run from a fresh clone
- **Status:** Fixed

## 1. Symptom

The solution compiles successfully in Visual Studio, but running
`OpenHardwareMonitor.exe` (Debug or Release) crashes instantly with no usable
UI. Exit code `-532462766` (`0xE0434352`, a managed exception).

## 2. Root cause

`OpenHardwareMonitor/Program.cs:12` is the very first statement executed:

```csharp
public static void Main()
{
    Crasher.Listen();
    ...
}
```

`Crasher` comes from the NuGet package **SergiyE.Common**. Both
`OpenHardwareMonitor.csproj` and `OpenHardwareMonitorLib.csproj` referenced it
with a floating version:

```xml
<PackageReference Include="SergiyE.Common" Version="1.*" />
```

`1.*` resolves to the newest match, which is **1.0.9745**. That public package
is not a functional runtime library — it is a stripped/gated "private" build in
which a large share of the public API is stubbed with
`throw new NotImplementedException()`.

### Evidence

Windows Error Reporting (Application event log) for the crash:

```
Faulting application name: OpenHardwareMonitor.exe
Faulting module name: KERNELBASE.dll    (0xE0434352)
Problem signature:
  P4: SergiyE.Common
  P5: 1.0.9745.0
  P7: 1d7
  P8: 5
  P9: System.NotImplementedException
```

The `.NET Runtime` event confirms the throwing method:

```
Exception Info: System.NotImplementedException
   at sergiye.Common.Crasher.Listen(Boolean)
   at OpenHardwareMonitor.Program.Main()
```

Method token `0x060001D7` was resolved via reflection to
`sergiye.Common.Crasher::Listen`. Disassembling `SergiyE.Common.dll` (ildasm)
showed its body is literally:

```il
.method public hidebysig static void Listen([opt] bool disableCrashDialog) cil managed
{
  IL_0000:  newobj  instance void [mscorlib]System.NotImplementedException::.ctor()
  IL_0005:  throw
}
```

Reflection over the whole assembly: **446 of 1140 methods** (39%) are stubbed
this way, including the entire startup path:

| Type | Stubbed members used at startup |
|------|---------------------------------|
| `Crasher` | `Listen` |
| `OSHelper` | `IsCompatible`, `IsUnix`, `Is64Bit`, `IsWindows8OrGreater`, `IsMetricSystemUsed`, `IsAdministrator` |
| `WinApiHelper` | `CheckRunningInstances` |
| `CommonHelper` | `GetApplicationPath` |
| `Updater` | `CheckForUpdates`, … |

Because `Crasher.Listen()` is the first call in `Main`, the process dies before
anything else can run, regardless of configuration.

### Version survey of the public package (api.nuget.org)

| Version | State |
|---------|-------|
| `1.0.9500`, `1.0.9577` | Fully functional |
| `1.0.9643` | Partially gated — `Crasher.Listen` throws `Exception("Demo version")` |
| `1.0.9644` … `1.0.9745` | Stubbed with `NotImplementedException` |

The NuGet page itself describes the package as
*"This is a private library, don't use it in your projects!"*, which is
consistent with the gated/stubbed builds: the released binaries are built by
the author against a licensed/private build, so the public repo + public
package cannot run out of the box.

## 3. The fix

Two changes, kept minimal:

1. **Pin `SergiyE.Common` / `SergiyE.Common.UI` to `1.0.9577`** — the newest
   public version that is complete and not license-gated.

2. **Add a compatibility alias.** Between `1.0.9577` and the gated builds the
   helper class was renamed from `OperatingSystemHelper` to `OSHelper`. Source
   code in this repo calls `OSHelper.*`, which only exists in the broken newer
   packages, so map the old name to the new type:

   `OpenHardwareMonitorLib/globalUsings.cs` and
   `OpenHardwareMonitor/AssemblyInfo.cs`:

   ```csharp
   global using OSHelper = sergiye.Common.OperatingSystemHelper;
   ```

Resulting diff:

```diff
-    <PackageReference Include="SergiyE.Common" Version="1.*" />
-    <PackageReference Include="SergiyE.Common.UI" Version="1.*" />
+    <PackageReference Include="SergiyE.Common" Version="1.0.9577" />
+    <PackageReference Include="SergiyE.Common.UI" Version="1.0.9577" />
```

(plus the same `1.0.9577` pin in `OpenHardwareMonitorLib.csproj` and the two
`global using` aliases.)

### Why this fix and not another

- **Why not `1.0.9643`+?** Those are gated (`"Demo version"`) or stubbed; they
  reproduce the same class of crash.
- **Why `1.0.9577` instead of `1.0.9500`?** Both are functional; `1.0.9577` is
  the newest non-gated build, minimizing API drift with the current source.
- **Why an alias instead of editing ~40 call sites?** The API surface used by
  this repo (`IsCompatible(bool, out string, out Action)`, `IsUnix`,
  `Is64Bit`, `IsWindows8OrGreater`, `IsMetricSystemUsed`, `IsAdministrator()`)
  is identical between `OperatingSystemHelper` (1.0.9577) and `OSHelper`
  (renamed later). A single alias preserves the source unchanged and keeps the
  change reversible. A compile test against `1.0.9577` failed with *only*
  `CS0103: The name 'OSHelper' does not exist` errors across all three
  projects — confirming the rename was the sole incompatibility.

## 4. Verification

1. Restored + rebuilt `OpenHardwareMonitor.sln` after the change — build
   succeeded (only the pre-existing `CS0067` warning).
2. Launched `OpenHardwareMonitor.exe` (Debug). The main window
   **"Open Hardware Monitor (DEBUG)"** opened and the process remained alive
   and responsive for 74+ seconds; no new `Application Error` / `.NET Runtime`
   events were generated.
3. Re-checked the Application event log: the only remaining crash entries are
   the pre-fix timestamps.

## 5. Follow-up / recommendation

- Consider replacing the floating `Version="1.*"` with a pinned, known-good
  version permanently, or vendoring/building `sergiye.Common` from source, so a
  future upstream publish cannot silently break startup again.
- Ideally add a smoke test that launches the built executable in CI; the
  existing `.github/workflows/auto-build.yml` only compiles, which is why this
  regression slipped through.
