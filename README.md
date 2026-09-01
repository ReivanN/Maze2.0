# Maze 2.0

**Maze 2.0** is a cooperative multiplayer 3D maze game built in Unity. Players spawn in different parts of a procedurally generated level and must explore the maze together, interact with various mechanisms, and reach the exit zone to complete the level.

## Key Features

- **Procedural level generation** — the maze is generated automatically on every run. Two modes are available:
  - Classic maze based on a recursive backtracking algorithm
  - Cave dungeon with two starting zones, a central corridor, doors, and buttons
- **Cooperative mechanics** — buttons work crosswise: the left button opens the right door, and the right button opens the left door. Players must cooperate to progress
- **Multiplayer networking** — host/client model via Unity Netcode for GameObjects and Unity Relay. Room-based join codes with no need to configure IP or ports
- **World synchronization** — player positions, door states, platforms, character colors, and visual effects are synchronized across all participants
- **Extra mechanics** — moving and falling platforms, laser traps, teleportation, customizable player color, voice chat via Vivox
- **User interface** — room creation and join, join code display, level progress indicator, and victory screen
