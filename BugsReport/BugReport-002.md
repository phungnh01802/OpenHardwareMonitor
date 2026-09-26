# Bug Report #2 — Windows Defender flags the WinRing0 driver on startup

- **Project:** OpenHardwareMonitor (HardwareMonitor/OpenHardwareMonitor)
- **Component:** `OpenHardwareMonitorLib/Hardware/Ring0.cs`, `OpenHardwareMonitor/Program.cs`, `OpenHardwareMonitor/UI/MainForm.cs`
- **OS:** Windows 11 (build 10.0.26100/26200)
- **Severity:** High — antivirus warning at every launch; in managed/enterprise
  environments the driver may be blocked or quarantined, disabling sensors
- **Status:** Mitigated (in-app opt-out)

## 1. Symptom

Running `OpenHardwareMonitor.exe` triggers the Windows Security notification:

```
VulnerableDriver:WinNT/WinRing0
```

Windows Defender (and other AV vendors that consume Microsoft's vulnerable
driver blocklist) reports the application because it drops and installs the
**WinRing0** kernel driver at startup.

## 2. Root cause

This is **not** a false positive in the classic sense. WinRing0 is on
Microsoft's *vulnerable driver blocklist*: it exposes unrestricted
arbitrary MSR/I/O-port/physical-memory/PCI access to user mode, and shipped
builds had known privilege-escalation issues (e.g. CVE-2020-14979). Defender
therefore flags the driver binary regardless of how it is used.

The application depends on the driver for low-level access:

- `OpenHardwareMonitorLib/Hardware/Ring0.cs:37` — creates the kernel driver
  object (`WinRing0_1_2_0`).
- `Ring0.cs:44` / `Extract()` at `Ring0.cs:92` — decompresses the embedded
  `WinRing0x64.gz` / `WinRing0.gz` resource, writes it to `%TEMP%\*.sys`, and
  installs it as the service `R0<AppName>` (`R0OpenHardwareMonitor`).
- `Computer.Open()` calls `Ring0.Open()` at `Computer.cs:515`.

Because the driver is embedded and installed automatically with no way to opt
out, every launch writes a blocklisted `.sys` to disk and loads it, which the
AV reacts to.

The driver is needed only for: CPU MSR/voltage/temperature, motherboard
Super-I/O (fans/voltages/temps), fan control, RAM SPD and some PCI/GPU
registers. Loads/clocks, storage, network, batteries, and vendor GPU APIs do
not require it.

## 3. The fix

Add an in-app opt-out so the driver is never extracted/installed, while keeping
the full sensor set available for users who accept the warning.

### 3.1 Command-line flag

`OpenHardwareMonitor/Program.cs` — `Main` now takes args; `--no-driver`
(aliases `--no-ring0`, `-nd`) disables the driver for the session without
changing the saved setting:

```csharp
bool disableDriver = HasNoDriverArgument(args);
...
using (MainForm form = new MainForm(disableDriver))
```

### 3.2 Persisted Options-menu toggle

`OpenHardwareMonitor/UI/MainForm.cs`:

- The constructor accepts `bool disableDriver = false` and resolves the
  effective state before opening the computer:

  ```csharp
  _driverEnabled = !disableDriver && _settings.GetValue("ring0MenuItem", true);
  ...
  _computer = new Computer(_settings);
  _computer.IsRing0Enabled = _driverEnabled;   // applied before Open()
  ```

- A checkable **Options → "WinRing0 Driver"** item is created and inserted into
  the Options menu. Toggling it persists `ring0MenuItem` and, because the
  driver state is fixed at `Computer.Open()`, shows a restart prompt:

  ```csharp
  private void DriverMenuItem_Click(object sender, EventArgs e)
  {
      bool enabled = _driverMenuItem.Checked;
      _settings.SetValue("ring0MenuItem", enabled);
      _computer.IsRing0Enabled = enabled;
      if (enabled != _driverEnabled)
          MessageBox.Show(this, "Restart OpenHardwareMonitor for the WinRing0 driver change to take effect.", ...);
  }
  ```

No library change was required: `Computer.Open()` already gates the call —
`if (IsRing0Enabled) Ring0.Open(portable);` (`Computer.cs:514`) — and
`MemoryGroup` skips the SPD/RAMSPD driver when Ring0 is not open
(`MemoryGroup.cs:26`).

## 4. Verification

1. Built the solution after the change — build succeeded.
2. Recorded the `%TEMP%\*.sys` contents, then launched:

   ```
   OpenHardwareMonitor.exe --no-driver
   ```

   The app started normally, and **no new `.sys` file was created** and the
   `R0OpenHardwareMonitor` driver service was not installed/loaded. Earlier
   default runs wrote `%TEMP%\tmpXXXX.sys` (the WinRing0 driver), which is the
   before/after control.
3. Runtime plumbing checked against the built library: with
   `Computer.IsRing0Enabled = false`, `Computer.Open()` completes and no driver
   file is produced.

## 5. Usage / trade-off

- Avoid the detection without changing app state:
  `OpenHardwareMonitor.exe --no-driver`
- Permanent opt-out: uncheck **Options → WinRing0 Driver** and restart.
- When disabled, low-level sensors are unavailable (CPU temperature/voltage,
  motherboard Super-I/O, fan control, RAM SPD, some GPU registers). Other groups
  (loads/clocks, memory, storage, network, NVIDIA/AMD via vendor drivers) still
  work.
- Users who need the full sensor set can instead exclude the app folder from
  antivirus scans (see the README's antivirus note); the driver remains
  inherently flagged because it is on Microsoft's vulnerable-driver blocklist.

## 6. Follow-up / recommendation

- Consider signing the build and evaluating a supported/allowlisted low-level
  access mechanism to remove the root cause rather than only offering an
  opt-out.
- Document the flag and menu option in the README so users understand the
  trade-off and how to re-enable the driver.
- A test that launches the build with `--no-driver` in CI would guard the
  opt-out path against regressions.
