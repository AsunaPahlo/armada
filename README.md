# <img src="https://github.com/AsunaPahlo/armada-web/blob/master/app/static/armada_logo.png" alt="Armada Logo" height="60"> Armada

A Dalamud plugin for FFXIV that uploads submarine fleet data to the Armada fleet tracking webui, enabling real-time submarine and airship voyage monitoring across your Free Company.

## Features

- Real-time submarine and airship tracking
- Automatic voyage status updates sent to the Armada backend
- Multi-account support with configurable nicknames
- Seamless integration with the Armada fleet management dashboard

## Installation

### Prerequisites

- XIVLauncher, FFXIV, Dalamud, AutoRetainer
- Access to an Armada webui instance (selfhosted or otherwise)

### Installing the Plugin

1. Open the Plugin Installer in-game using `/xlplugins`
2. Search for "Armada" and install
3. Configure the plugin settings (see below)

## Configuration

### Getting an API Key

To connect the plugin to the Armada webui, you need to generate an API key:

1. Log in to the Armada webui as an administrator
2. Click the **Settings gear** icon in the top right corner
3. Click the **Settings** button
4. Click **"API Keys"** from the left menu
5. Enter a name for your key (e.g., "Main Account")
6. Click **"Create Key"**
7. Copy the API key that appears in the popup

> **Tip:** Create one API key per client/account. This helps you identify which account is sending data and makes it easier to manage access.

### Plugin Settings

Open the plugin settings window using `/armada` or through the Plugin Installer:

| Setting | Description |
|---------|-------------|
| **API Key** | The API key generated from the Armada webui |
| **Server URL** | The URL of your Armada webui instance (e.g. wss://armada.mydomain.com |
| **Nickname** | Display name shown in the webui for this account's submarines and Free Companies. Use this to identify which game account/character the data is coming from. |

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

## License

This project is licensed under the MIT License.
