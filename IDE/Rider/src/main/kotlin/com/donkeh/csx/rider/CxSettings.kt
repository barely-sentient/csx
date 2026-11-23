package com.donkeh.csx.rider

import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage

/** Global CSX plugin settings (like VS Code user settings), stored in csx-rider.xml. */
@Service(Service.Level.APP)
@State(name = "CSXSettings", storages = [Storage("csx-rider.xml")])
class CxSettings : PersistentStateComponent<CxSettings.State> {
    var serverMode: String = "local"
    var serverPath: String = "csx"
    var serverArgs: String = "lang-server"
    var localProjectPath: String = ""

    override fun getState(): State = State().apply {
        this.serverMode = this@CxSettings.serverMode
        this.serverPath = this@CxSettings.serverPath
        this.serverArgs = this@CxSettings.serverArgs
        this.localProjectPath = this@CxSettings.localProjectPath
    }

    override fun loadState(state: State) {
        // XmlSerializerUtil.copyBean requires identical bean classes. `state` is CxSettings.State
        // while `this` is CxSettings, so copying manually avoids the platform assertion that
        // previously prevented any saved CSX settings from loading.
        serverMode = state.serverMode
        serverPath = state.serverPath
        serverArgs = state.serverArgs
        localProjectPath = state.localProjectPath
    }

    class State {
        var serverMode: String = "local"
        var serverPath: String = "csx"
        var serverArgs: String = "lang-server"
        var localProjectPath: String = ""
    }

    companion object {
        const val MODE_LOCAL = "local"
        const val MODE_INSTALLED = "installed"
    }
}
