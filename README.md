=== Does loading take forever? This mod is for you! ===

Sick of staring at the loading screen every time you start RimWorld? This mod cuts down startup time using several tricks that work in the background, so the game loads faster without changing any gameplay.

--- What it does ---

- Starts loading mod content earlier, during moments the game would otherwise sit idle.
- Optionally delays non-essential textures (like background objects) until after you enter a game — weapons, clothing, food, pawns, and furniture are always loaded right away so nothing looks broken. (disabled by default)
- Smarter atlas baking: by default, uses RimWorld's original baking process. When enabled, automatically adjusts batch sizes based on your GPU speed to reduce loading hitches. Enabling this is optional but recommended for high mod counts.
- Texture downscaler: reduces the file size of high-resolution textures to save VRAM and speed up loading. Your original mod files are never touched — downscaled copies are stored in a separate folder, and you can delete them anytime to go back to full quality. (disabled by default)

--- Things to know ---

- The game may briefly freeze or feel unresponsive during startup. This is normal — the mod is doing work in the background.
- When first entering a game with delayed texture loading enabled, some objects may briefly show as pink/grey placeholders. They fill in within a few seconds.
- The texture downscaler and atlas baking options are optional. Atlas baking always runs in some form — disabling the option just uses the vanilla baking process instead of the adaptive one.

--- Works well with ---

- Loading Progress
- DefLoadCache
- Image Opt

--- Works best with a lot of mods. Zero impact on gameplay or saves. ---

Original mod by Taranchuk. This is a maintained fork with bug fixes and improvements.