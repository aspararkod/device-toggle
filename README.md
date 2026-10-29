# Device Toggle

Some programs misbehave while a certain device is plugged in: a joystick that makes menus scroll by themselves, a controller that steals input, and so on. Device Toggle turns devices off and on in Windows, so you don't have to unplug cables.

It is a single file, `DeviceToggle.exe`. Nothing to install; it runs on any Windows 10 or 11 PC.

## Using it

1. Run `DeviceToggle.exe` and click **Yes** on the Windows admin prompt. Windows only lets administrators turn devices on and off.
2. Everything plugged into your USB ports, plus Bluetooth input devices, is listed by name and grouped by type: game controllers, keyboards and mice, audio, and so on.
3. Select a device and click **Turn off** or **Turn on**, or double-click it. Turning off a device is the same as unplugging it.
   - A device you turn off stays off, even after a restart, until you turn it on again.
   - You get a warning before turning off a keyboard or mouse.

### Launching a program with devices turned off

1. Under **Launch a program with devices turned off**, click **Add...**, pick the program's `.exe`, and tick the devices to turn off.
2. Click **Launch**. The devices turn off and the program starts. When it closes, the devices turn back on.
3. **Desktop shortcut** creates a shortcut that does all of this in one double-click.

**When devices come back on:** many programs are started through a launcher that opens the real program and then closes. Device Toggle handles this by itself: devices stay off until **everything running from the program's folder** has closed. The status bar shows what it is waiting for.

If you close the window while a launched program is running, Device Toggle keeps running in the system tray. Right-click the tray icon for **Turn devices back on now** or **Exit**.

## Notes

- **"Windows protected your PC" warning:** the exe is not code-signed, so Windows SmartScreen may warn the first time. Click **More info** → **Run anyway**.
- **How it works:** it uses the same Windows functions as Device Manager's **Disable device** / **Enable device**. It never touches other programs or games. Programs are started as your normal user, not as admin, exactly as if you double-clicked them.
- **Settings:** stored in `%APPDATA%\DeviceToggle\settings.ini`.
- **Shortcuts:** desktop shortcuts point to wherever `DeviceToggle.exe` was when you made them, so keep the exe in one place.

## Building

Run `build.cmd`. It uses the C# compiler that comes with Windows, so no SDK is needed. The output is `dist\DeviceToggle.exe`.
