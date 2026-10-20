# URAH_Loader

BepInEx 5 plugin for Knights of Honor II: Sovereign. It loads the URAH plugins that come with a mod
(Steam Workshop or the local Mods folder), so players install BepInEx and this loader once and then
get plugin updates through the mod. It only runs DLLs that are listed in a manifest signed by the
URAH release key: a Workshop item from someone else can not slip code in.

## How it works

- A mod carries `plugins/manifest.txt`, `plugins/manifest.sig` and the DLLs it lists:
  ```
  format 1
  mod URAH_HRE
  dll URAH_HRE.dll urah.koh2.hre 0.26.0 sha256:<hex>
  dll URAH_MapKit.dll urah.koh2.mapkit 0.1.0 sha256:<hex>
  file some.bundle sha256:<hex>
  ```
  `manifest.sig` is RSA-3072 PKCS#1 v1.5 over SHA-256 of `manifest.txt`. The public keys are built
  into the loader (`plugin/Signature.cs`).
- The loader follows the game's mod list (`ModManager.OnActiveModsChanged`). When a mod becomes
  active, it verifies the signature and every hash, loads the DLLs from the checked bytes
  (`Assembly.Load(byte[])`, never from the path) and starts their plugins in dependency order.
- Only files named in a signed manifest are touched; anything else in a mod folder is ignored.
- A plugin in two mods (URAH_MapKit) is loaded once, the highest version. A plugin that is already
  loaded by BepInEx from `BepInEx/plugins` (a development build) wins over the mod's copy.
- A loaded plugin stays loaded when its mod is switched off (Mono can not unload); the plugin's own
  mod link removes its patches.
- Plugins loaded this way have no `Assembly.Location`. Their `plugins/` folder is
  `AppDomain.CurrentDomain.GetData("urah.loader.dir:" + assemblyName)`.

## Install (players)

Install BepInEx 5 (x64) into the game folder, then put `URAH_Loader.dll` into
`BepInEx/plugins/URAH_Loader/`. Subscribe to the URAH mods on the Workshop and switch them on in the
game's Mods menu.

## Releases

Built and signed releases are in [URAH_koh2_releases](https://github.com/URAHDangerzone/URAH_koh2_releases/releases?q=URAH_Loader&expanded=true): [`URAH_Loader-v<version>`](https://github.com/URAHDangerzone/URAH_koh2_releases/releases?q=URAH_Loader&expanded=true) is this plugin alone
(`BepInEx/plugins/URAH_Loader/`), [`URAH_Starter`](https://github.com/URAHDangerzone/URAH_koh2_releases/releases?q=URAH_Starter&expanded=true) bundles BepInEx 5 with URAH_Loader, URAH_Net and URAH_PluginManager.
Every release is signed with the URAH release key; [URAH_koh2_releases](https://github.com/URAHDangerzone/URAH_koh2_releases) explains how to check it.

## Development

```
cd plugin && dotnet build -c Release -p:Deploy=true
```

builds against your game install. Extra public keys in `BepInEx/config/urah.loader/trusted_keys/*.xml`
are trusted too: that is for testing with a local dev key and needs no change to the loader.
Writing to that folder needs the same access as putting a DLL into `BepInEx/plugins`.
