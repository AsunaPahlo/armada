# Armada

A Dalamud plugin for FINAL FANTASY XIV that connects to the Armada fleet tracking webui, enabling real-time submarine and airship voyage monitoring across your Free Company.

## Features

- Real-time submarine and airship tracking
- Automatic voyage status updates sent to the Armada webui
- Multi-account support with configurable nicknames
- Seamless integration with the Armada fleet management dashboard

## Installation

### Prerequisites

- XIVLauncher, FINAL FANTASY XIV, and Dalamud have all been installed and the game has been run with Dalamud at least once.
- Access to an Armada webui instance (self-hosted or provided by your FC)

### Installing the Plugin

1. Open the Plugin Installer in-game using `/xlplugins`
2. Search for "Armada" and install
3. Configure the plugin settings (see below)

## Configuration

### Getting an API Key

To connect the plugin to the Armada webui, you need to generate an API key:

1. Log in to the Armada webui as an administrator
2. Click the **Settings gear** icon in the top right corner
3. Click **"API Keys"** from the menu
4. Enter a name for your key (e.g., "Main Account")
5. Click **"Create Key"**
6. Copy the API key that appears in the popup

> **Tip:** Create one API key per client plugin/game account. This helps you identify which account is sending data and makes it easier to manage access.

### Plugin Settings

Open the plugin settings window using `/armada` or through the Plugin Installer:

| Setting | Description |
|---------|-------------|
| **API Key** | The API key generated from the Armada webui |
| **Server URL** | The URL of your Armada webui instance |
| **Nickname** | Display name shown in the webui for this account's submarines and Free Companies. Use this to identify which game account/character the data is coming from. |

## Status Indicators

The plugin displays connection and sync status to help you monitor its operation:

| Status | Meaning |
|--------|---------|
| **Connected** | Successfully connected to the Armada webui and sending data |
| **Disconnected** | Not connected to the webui; check your settings and network |
| **Syncing** | Currently uploading voyage data to the server |
| **Error** | A problem occurred; check the Dalamud log for details |
| **Authenticating** | Validating API key with the server |
| **Invalid API Key** | The configured API key is invalid or expired; generate a new one |
| **Server Unreachable** | Cannot reach the Armada webui; verify the server URL and network connectivity |
| **Rate Limited** | Too many requests sent; the plugin will automatically retry |

## Troubleshooting

### Plugin won't connect
1. Verify the Server URL is correct and accessible from your network
2. Ensure the API key is valid and has not been revoked
3. Check if the Armada webui server is running

### Data not appearing in webui
1. Confirm the plugin shows "Connected" status
2. Make sure you have submarines or airships deployed on voyages
3. Check that your account has the necessary permissions in the webui

### API key not working
1. Log in to the webui and verify the key still exists in API Keys settings
2. Generate a new API key and update the plugin settings
3. Ensure you copied the entire key without extra spaces

## Building from Source

### Prerequisites

- .NET 8 SDK
- XIVLauncher with Dalamud installed

### Build Steps

1. Clone this repository
2. Open `Armada.sln` in Visual Studio 2022 or JetBrains Rider
3. Build the solution
4. The plugin DLL will be at `Armada/bin/x64/Debug/Armada.dll`

### Development Setup

1. Use `/xlsettings` in-game to open Dalamud settings
2. Go to `Experimental` and add the path to your built DLL to Dev Plugin Locations
3. Use `/xlplugins` and enable the plugin from Dev Tools > Installed Dev Plugins

## License

This project is licensed under the MIT License.
