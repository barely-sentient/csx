package com.donkeh.csx.rider

import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.util.xmlb.XmlSerializerUtil

/** Persistent CSX plugin settings, stored in csx-rider.xml. */
@Service(Service.Level.PROJECT)
@State(name = "CSXSettings", storages = [Storage("csx-rider.xml")])
class CxSettings : PersistentStateComponent<CxSettings.State> {
    var serverMode: String = "local"
    var serverPath: String = "csx"
    var localProjectPath: String = ""

    override fun getState(): State = State().apply {
        this.serverMode = this@CxSettings.serverMode
        this.serverPath = this@CxSettings.serverPath
        this.localProjectPath = this@CxSettings.localProjectPath
    }

    override fun loadState(state: State) {
        XmlSerializerUtil.copyBean(state, this)
    }

    class State {
        var serverMode: String = "local"
        var serverPath: String = "csx"
        var localProjectPath: String = ""
    }

    companion object {
        const val MODE_LOCAL = "local"
        const val MODE_INSTALLED = "installed"
    }
}
