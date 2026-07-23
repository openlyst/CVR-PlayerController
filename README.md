# What is this?

This mod lets one player remotely control another in ChilloutVR.
Both users must have the mod installed and enable consent options.

## Installation

The mod requires [BTKUILib](https://github.com/BTK-Development/BTKUILib) and [MelonLoader](https://github.com/lavagang/melonloader).
Download the latest release DLL and place it into your Mods folder next to your ChilloutVR executable.

## Features

- **Per-limb consent system.** Allow the whole body, or specific areas of your choosing.
- **Emergency release.** Press jump 10 times rapidly, or press tilde (~) to break current control.
- **User whitelist.** Approve only who you want without fear of unapproved users attempting control.
- **Entirely routed through the Mod Network.** No need to host or connect to external servers.
- **Voice routing.** Speak through the mouth of your chosen target.
- **Full parameter sync.** What you change while in control is relayed to all, including face tracking parameters.
- **Voice side-channel.** During a control session, both users hear each other in "2D" as if it were a voice call.

## How to use

**To be controlled**, enable "Allow being controlled" from the mod's BTKUI tab and configure your permission preferences and optionally a whitelist.

**To control another user**, ensure that the target has control consent enabled and their whitelist is configured properly, and click the user's name from the control tab.

## Building

Clone the repo and run:

```
dotnet build src/PlayerRemoteControl.csproj -c Release
```
