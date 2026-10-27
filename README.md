<h1 align="center">GTWM Model Converter</h1>

<p align="center">Turn almost any 3D model into a ready-to-wear <b>Get To Work</b> character: drop it in, get a <code>.glb</code> out.
Fitting, rigging and skin weights are automatic.</p>

<p align="center">

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE) ![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey) ![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4) [![Works with Model Changer](https://img.shields.io/badge/works%20with-GTWM%20Model%20Changer-2ea44f)](https://github.com/BHXgg/GTWM-ModelChanger)

</p>

A desktop companion to the [GTWM Model Changer](https://github.com/BHXgg/GTWM-ModelChanger) mod. One self-contained
`.exe`; no Blender or other software needed.

## Features

- **Reads** `.fbx`, `.obj`, `.glb`, `.gltf`, `.vrm`, `.dae`, `.pmx`, `.3ds` and everything else [Assimp](https://github.com/assimp/assimp) supports.
- **Rigged humanoids** (Mixamo, VRoid, Unity, Unreal, Blender and similar) are recognised and **retargeted** onto the game skeleton: the model is posed to match the game character and keeps its own skin weights.
- **Unrigged models** (statues, props, creatures) are **auto-rigged** by proximity to the game's bones.
- **Automatic fitting**: Z-up models are stood up, everything is scaled to the character's height and placed on the floor. Tick *Turn around 180°* if a model faces backwards.
- **Textures** are found next to the model, in `textures` subfolders or embedded, then packed into the output and shrunk until the file is under 20 MB (PNG, JPG, TGA, BMP, GIF and TIFF).
- **Live preview** of the result, and a log explaining every decision.

## Getting started

1. Install the [GTWM loader](https://github.com/BHXgg/GTWM) and [Model Changer](https://github.com/BHXgg/GTWM-ModelChanger).
2. Start the game and **load into any level once**. Model Changer writes the game skeleton to
   `GTWM\templates\character_template.glb`; the converter needs it.
3. Download `GTWM-ModelConverter-1.0.0.zip` from [Releases](https://github.com/BHXgg/GTWM-ModelConverter/releases) and
   extract it into the game folder (the exe lands in `GTWM\tools`).
4. Run **GTWM Model Converter.exe**. The *Game skeleton* box should say **Found**.
5. Drop a model on the window, optionally give it a name, and wait for *Saved*. The result goes into `GTWM\models`.
6. In game, press **F9** and pick it. In [Multiplayer](https://github.com/BHXgg/GTWM-Multiplayer), other players
   get it automatically.

### Tips

- Only the base-colour texture is used; normal, roughness and emissive maps are ignored.
- Missing textures are listed in the log (*Texture not found: …*). Put that file next to the model and convert again.
- DDS and PSD textures can't be read; convert them to PNG first.

## Command line

```powershell
"GTWM Model Converter.exe" --convert <model> <character_template.glb> <out.glb> [--turn]
"GTWM Model Converter.exe" --selftest <empty folder>
```

## Building from source

**Requirements:** Windows and the [.NET SDK](https://dotnet.microsoft.com/download) 6.0 or newer. The game is **not**
needed to build.

```powershell
git clone https://github.com/BHXgg/GTWM-ModelConverter.git
cd GTWM-ModelConverter
.\build.ps1             # -> build\ModelConverter\GTWM Model Converter.exe
.\build.ps1 -SelfTest   # build and run the built-in tests
.\build.ps1 -Zip        # also produce the release zip
```

AssimpNet, the native Assimp library and Newtonsoft.Json are embedded in the exe as resources and loaded at
runtime (the native DLL is extracted to `%LOCALAPPDATA%\GTWM\ModelConverter`), so the build output is a single file.

| File | Role |
|---|---|
| `Importer.cs` | Reads any format through Assimp, resolves textures |
| `Humanoid.cs` | Recognises humanoid bones from naming conventions |
| `Template.cs` | Loads the game skeleton and body exported by Model Changer |
| `Converter.cs` | Fit, bone mapping, re-posing, weight transfer and proximity weights |
| `Glb.cs` | glTF 2.0 binary reader/writer |
| `MainForm.cs`, `Preview` | The window and the preview renderer |
| `SelfTest.cs` | Round-trip tests (FBX, static meshes, skin validity) |

## AI assistance disclosure

Parts of this project were developed with the help of an AI coding assistant (Anthropic's Claude), mainly the
user-interface layout and some of the multiplayer networking code. Treat it like any other community mod: read the
code if you want to know exactly what it does, and please report anything that looks wrong.

## Disclaimer

This is an unofficial, fan-made project. It is not affiliated with, endorsed by or supported by the developers or
publishers of *Get To Work*. No game files are included in this repository or its releases; you need your own copy
of the game. Mods change how the game runs: back up your saves, and don't report bugs you hit while modded to the
game's developers.

## License

[MIT](LICENSE) © 2026 BHXgg. Bundled libraries are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
