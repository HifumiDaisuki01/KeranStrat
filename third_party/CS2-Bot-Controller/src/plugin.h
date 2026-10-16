// Metamod plugin lifecycle and metadata declarations.
#pragma once

#include <ISmmPlugin.h>

namespace cs2bc {

class BotControllerPlugin : public ISmmPlugin
{
  public:
    // Loads interfaces and installs the controller modules.
    bool Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late) override;
    // Removes hooks and releases controller state.
    bool Unload(char* error, size_t maxlen) override;
    // Accepts a plugin pause request.
    bool Pause(char* error, size_t maxlen) override;
    // Accepts a plugin resume request.
    bool Unpause(char* error, size_t maxlen) override;
    // Handles completion of Metamod plugin loading.
    void AllPluginsLoaded() override;

    // Returns plugin author metadata.
    const char* GetAuthor() override;
    // Returns the plugin name.
    const char* GetName() override;
    // Returns the plugin description.
    const char* GetDescription() override;
    // Returns the plugin project URL.
    const char* GetURL() override;
    // Returns the plugin license.
    const char* GetLicense() override;
    // Returns the version supplied by the build.
    const char* GetVersion() override;
    // Returns the compilation date and time.
    const char* GetDate() override;
    // Returns the plugin log tag.
    const char* GetLogTag() override;

  private:
    bool m_convarsRegistered = false;
};

extern BotControllerPlugin g_plugin;

} // namespace cs2bc

PLUGIN_GLOBALVARS();
