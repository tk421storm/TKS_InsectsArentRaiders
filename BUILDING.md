# Building Insects Aren't Raiders

## RimWorld 1.7 onwards (.NET 10)

Project: `InsectsArentRaiders.1.7.csproj`, solution: `InsectsArentRaiders.1.7.sln`.

Requirements:
- .NET 10 SDK (`dotnet --list-sdks` should list a `10.0.x` entry)
- RimWorld on 1.7 in Steam: the project references `Assembly-CSharp.dll`, `UnityStubs.dll` and `Waylith.dll`
  from the RimWorld folder two levels up (`..\..\`), which only exist on 1.7
- Harmony from the Workshop (2009463077), referenced from its `1.7\Assemblies` folder

From this folder:

```
dotnet build InsectsArentRaiders.1.7.sln -c 1.7
```

The DLL and pdb are written to `1.7\Assemblies\`. The `1.7` configuration defines `v1_7`.

Visual Studio 2022 cannot open this project for building (the .NET 10 SDK needs MSBuild 18, which ships
with Visual Studio 2026). Use the command above, or Visual Studio 2026 with the `1.7` configuration.

## RimWorld 1.6 (.NET Framework 4.8)

Project: `InsectsArentRaiders.csproj`, solution: `InsectsArentRaiders.sln`, configuration `1.6`.
This needs the game on 1.6 (it references `..\..\RimWorldWin64_Data\Managed`) and builds with Visual Studio 2022
or its MSBuild:

```
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" InsectsArentRaiders.csproj -p:Configuration=1.6
```


Translations live in the root `Languages` folder, which the game loads for every version.
