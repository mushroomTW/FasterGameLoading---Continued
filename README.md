Maintained fork with improvements and bug fixes.

This mod significantly speeds up game loading through various optimization technologies, especially suitable for players with many mods. Key features include:

1. **Early Mod Content Loading**: Preloads mod content during idle periods at startup. May cause brief unresponsiveness during loading - this is expected behavior.
2. **Delayed Graphic Loading (Advanced)**: Defers loading of non-essential graphics until gameplay. Essential items (weapons, apparel, food, pawns, linked buildings, medicine, furniture) are loaded immediately to prevent visual issues. Other graphics load gradually in the background with minimal performance impact.
3. **Adaptive Static Atlas Baking (Advanced)**: Improved atlas baking that dynamically adjusts batch sizes based on GPU performance. Can be disabled if you experience specific issues, though this may reduce in-game rendering performance (lower TPS).
4. **Texture Optimization Tool**: Built-in tool to downscale high-resolution textures and clean up redundant DDS files.


Note: Delayed loading may cause temporary texture placeholders when first entering a game. These will resolve as graphics load in the background.
