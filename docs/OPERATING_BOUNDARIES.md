# Operating boundaries

GSwitcher was developed around a Dell G16 Windows environment. Hardware and firmware behavior varies; this source publication is not a claim of universal laptop compatibility.

- **Power plans:** mode discovery expects recognizable installed plans or known legacy identifiers. Custom plan GUIDs in the source are compatibility references, not credentials. The repository does not install the author's personal plans.
- **Processor and thermal controls:** explicit actions can change Windows processor power settings and supported Dell profiles. Automatic cooling is disabled by default in the source settings constructor. Calibration intentionally creates CPU load, capped at four worker threads.
- **GPU:** dashboard route actions display status. Backend Dell WMI MUX and NVIDIA panel automation remain in the source, and route operations may require elevation or restart. NVIDIA policy availability depends on the driver and environment. Do not infer universal MUX support from these classes.
- **Permissions:** protected operations can request UAC consent and use one-shot scheduled tasks. This is application functionality, not private authentication material.
- **Optional automation:** enabling features can affect startup registration, power modes, microphone mute, or BITS pause/resume behavior. Review settings before enabling them.
- **Local data:** user settings are written next to the executable. Runtime data is designed to live in temporary folders; cleanup must be checked on the user's environment. Diagnostics can contain machine details; do not post raw logs without reviewing them.
- **Verification scope:** successful compilation and structural checks do not prove physical hotkey delivery, firmware behavior, or every background feature. Those require runtime checks on the target device.

No existing device configuration is bundled in this source repository.
