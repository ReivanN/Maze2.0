# Maze Together (Maze2.0)

A cooperative online 3D maze game for **two players**, made in Unity. Every level is generated procedurally, and both players have to reach the exit together. Doors open only when the other player steps on the matching button, floors collapse, lasers block the way, and every level comes with a random rule (a *mutator*) that changes how the game plays.

*Кратко по-русски: кооперативная онлайн-игра-лабиринт на двоих на Unity. Уровни генерируются процедурно, игроки подключаются по коду комнаты через Unity Relay, и должны вместе дойти до выхода. На каждом уровне действует случайный мутатор (гравитация, лёд, зеркальное управление, ветер, темнота).*

## Features

- Procedural cave / maze generator; the levels grow bigger as you progress.
- Online co-op for 2 players: host creates a room and shares a join code (Unity Relay, encrypted with DTLS).
- Colour-coded buttons and doors that have to be solved together.
- Falling platforms, moving platforms and laser traps with switches.
- Level mutators, a new random one on every level: **Low Gravity, Heavy World, Ice, Mirror Controls, Wind, Fog & Darkness**.
- Level timer, pause menu, display settings (fullscreen / resolution) that are remembered between sessions.

## How to play

1. One player presses **Create Room** and sends the code shown in the lobby to the other player.
2. The second player presses **Join Room**, enters the code and presses **Connect**.
3. When both players are in the lobby, the host presses **Start Game**.
4. Reach the exit. Both players have to stand in the exit zone at the same time to finish the level.

| Key | Action |
| --- | --- |
| W A S D | Move |
| Space | Jump |
| Left Shift | Sprint |
| Esc | Pause menu |

The game needs an internet connection (Unity Relay is an online service). There is no single-player mode: the level cannot be finished by one player.

## Requirements

- **Unity 6000.4.10f1** (see `ProjectSettings/ProjectVersion.txt`), Universal Render Pipeline.
- Packages are installed automatically from `Packages/manifest.json` (Netcode for GameObjects, Unity Services Multiplayer / Relay, Cinemachine, Input System, TextMesh Pro).
- A Unity account with this project linked to a **Unity Cloud project** (Project Settings > Services). In the Unity Cloud dashboard enable **Relay**. Players sign in anonymously, no accounts are needed in the game.

## Run from the editor

1. Open the project in Unity.
2. Open `Assets/Scenes/LevelScene.unity` and press Play. The menu is created by `RelayManager`.
3. To test with two players on one computer, use **Multiplayer Play Mode** (Window > Multiplayer > Multiplayer Play Mode) or a built copy of the game.

### One-time setup after pulling the mutators

Run **Maze > Setup > Apply All (Mutators + Release Settings)** once. It adds `LevelMutatorManager` to `LevelScene`, sets the application identifier and the app icon.

## Project structure

| Path | What is there |
| --- | --- |
| `Assets/Scripts` | Game code: `RelayManager` (menu, Relay, lobby, timer), `CaveDungeonGenerator`, `DungeonProgressManager`, doors, buttons, platforms, lasers |
| `Assets/Scripts/Mutators` | Level mutators (`LevelMutatorManager`, `PlayerMutatorApplier`, `LevelMutatorType`) |
| `Assets/Editor` | `MazeProjectSetup`: one-click project setup |
| `Assets/Prefabs`, `Assets/Scenes` | Game prefabs and `LevelScene` |
| `Дипломная работа.md` | Text of the diploma work about this project (Russian) |

## Known limitations

- Exactly 2 players (`maxPlayers = 2`).
- The player position is client-authoritative (`ClientNetworkTransform`) and there is no connection approval, so this is a friendly co-op game, not a cheat-proof one.
- Own sound effects and music are not added yet.
- Automated tests are not written yet.

## Third-party content

- [Unity Starter Assets: Third Person Controller](Assets/StarterAssets/license.txt): Unity Companion License.
- Grid Master (`Assets/Decimate`) and BTM Items Gems (`Assets/BTM_Assets`): third-party asset packs that come with their own documentation. Check their licenses before publishing the source code publicly, many asset licenses do not allow redistributing the files in a public repository.
- Unity packages (Netcode for GameObjects, Relay, Cinemachine, TextMesh Pro and others) follow their own Unity licenses.

## License

No license has been chosen yet. Until one is added, all rights are reserved by the author.
