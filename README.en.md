[简体中文](README.md) | English

# Probably Stolen-Skip

**Skip Dialogue for Probably Stolen**

By **Clemm** · Mod version **0.1.2** · Target game version **0.46D**

Speed up, skip, or automatically advance the opening dialogue that trading NPCs deliver before buying, selling, or discount options become available.

The mod's in-game display name is **深空当铺：允许跳过对话**. Version 0.1.2 uses Chinese text for its controls and settings. The labels below match those in-game strings.

## Controls

- Hold **F6** to smoothly accelerate the current trading introduction, up to 6× speed. A fast-forward indicator and an elastic dialogue-bubble animation provide feedback. Release the key to gradually return to normal speed.
- Press **F7** to skip the current trading introduction through a brief full-screen fade to black and back.
- The quick panel's `自动跳过前置对话` option (automatically skip opening dialogue) is off by default. Once enabled, it can automatically advance introductions for NPCs already encountered in the current save. During a first encounter, manual acceleration and skipping are still available.
- First encounters use the game's existing visit history, tracked by NPC type. Visits recorded before installing the mod also count. Each save has its own history, and random portraits belonging to the same NPC type share that type's record.
- The panel shows your actual key bindings. On first use, the mod chooses defaults that avoid occupied vanilla bindings and bindings registered by other mods through the official options system. It also reports identifiable binding conflicts.
- Use the panel's key-binding settings button to open the game's options, then edit the two bindings labelled with `深空当铺：允许跳过对话`.
- To reopen a closed panel, enable `显示深空当铺：允许跳过对话面板` (show the mod's panel) in the game's Dialogue options section, labelled `对话` in Chinese.
- Dialogue controls wait while you are entering text, the console has focus, the game's options or ESC pause menu are open, or the pointer is over the quick panel. Move the pointer away from the panel to resume dialogue controls.

Dialogue actions, completion callbacks, trading, discounts, quests, and rewards continue through the game's original logic. Players make dialogue choices themselves.

## Installation

1. Download `DialogueFlow-0.1.2.zip` from this repository's Releases page.
2. Extract the archive and place the complete `dialogue_flow` folder inside the game's `Mods` directory.
3. Check that the resulting paths include `Mods/dialogue_flow/manifest.xml` and `Mods/dialogue_flow/DialogueFlow.dll`.
4. Review and enable the mod in the game's mod menu, then restart the game.

For updates, exit the game and back up the existing `Mods/dialogue_flow` folder before copying in the complete new version. Use one installation source when choosing between a manual copy and a Steam Workshop subscription, so you can identify which version the game loads.

To remove the mod, disable it in the mod menu and restart the game, then delete `Mods/dialogue_flow`. The mod's official configuration stores the automatic-skipping preference and key bindings. The game manages NPC visit history in its saves.

Download packages and version notes are available on the Releases page. [Releases](https://github.com/ukpkmkk91/Probably-Stolen-Skip/releases)

## Validation and Known Limitations

The 0.1.2 release passed compilation with zero warnings, 1,969 motion assertions, the game's official scanner in strict offline whole-folder mode covering 103 methods, and release-file consistency checks. The distributed package was built with Roslyn.

In-game testing of 0.1.0 covered representative manual skips, automatic handling of repeat visits, first-encounter protection, saving and loading, separate histories in new saves, returning to another save, rebinding, and vanilla key-conflict warnings. Version 0.1.1 separately verified input protection inside the ESC pause menu and dialogue controls after resuming. Version 0.1.2 updates the display name and version metadata. The new in-game name and longer title layout are awaiting a separate in-game check.

- The default upper-right quick panel may cover part of the game's trading-window title. It uses the official dragging API, and its actual dragging behavior still needs confirmation.
- The subjective feel of the elastic animation while holding F6 still needs further confirmation.
- Actual key conflicts with other mods still need in-game testing. Detection covers vanilla bindings and bindings publicly registered through the official options system.
- Test coverage remains incomplete for all NPCs and quest variants, a supported trading-introduction sequence reaching a player-choice node, exits at precise points during the fade, and rapid repeated input.

## Building from Source

The project targets `netstandard2.1`. You need a .NET SDK supporting this target and a local installation of the target game version. Then run the following PowerShell command. Replace the example game path with your own installation directory.

```powershell
dotnet build DialogueFlow.csproj -c Release '-p:GameDir=C:\Games\Probably Stolen'
```

The resulting DLL is written to `bin/Release/netstandard2.1/DialogueFlow.dll`. Create a `dialogue_flow` folder and place the DLL, the repository's root `manifest.xml`, and the player instructions inside it, then follow the installation steps. Build references come from `GameDir/Probably Stolen_Data/Managed`.

The SDK command above is awaiting a test build in the release environment. The distributed package was built with Roslyn and the game's libraries. Self-built binaries require their own scanner and in-game validation.

## Development References

Development follows the official Probably Stolen modding documentation and the 何小鲁 framework. The mod author is Clemm. [Official modding documentation](https://github.com/dragonpan2/probably-stolen-modding/blob/main/README.zh-CN.md)
