package com.donkeh.csx.rider

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.options.Configurable
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.TextFieldWithBrowseButton
import com.intellij.ui.components.JBRadioButton
import com.intellij.util.ui.FormBuilder
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.ButtonGroup

/** Settings UI for CSX under Settings | Languages & Frameworks | CSX. */
class CxConfigurable(private val project: Project) : Configurable {
    private val localRadio = JBRadioButton("Local build (development)")
    private val installedRadio = JBRadioButton("NuGet build (csx on PATH)")
    private val serverPathField = TextFieldWithBrowseButton()
    private val serverArgsField = TextFieldWithBrowseButton()
    private val localPathField = TextFieldWithBrowseButton()
    private var panel: JPanel? = null

    override fun getDisplayName(): String = "CSX"

    override fun createComponent(): JComponent {
        val group = ButtonGroup()
        group.add(localRadio)
        group.add(installedRadio)

        localPathField.addBrowseFolderListener(
            "Select CSX CLI Tool",
            "Path to CSX.CliTool.exe, .dll, .csproj, or its directory",
            project,
            com.intellij.openapi.fileChooser.FileChooserDescriptor(
                true, true, false, false, false, false
            )
        )

        panel = FormBuilder.createFormBuilder()
            .addLabeledComponent("Server mode:", JPanel().apply {
                add(localRadio)
                add(installedRadio)
            })
            .addLabeledComponent("Server command:", serverPathField)
            .addLabeledComponent("Server args:", serverArgsField)
            .addLabeledComponent("Local CSX path:", localPathField)
            .addComponent(JPanel().apply {
                add(javax.swing.JLabel(
                    "Matches VS Code: server.mode, server.path, server.args, local.projectPath.<br>" +
                    "Local: CSX.CliTool.exe/.dll/.csproj or its directory (auto-detected if blank).<br>" +
                    "NuGet: the 'csx' tool from NuGet, on PATH. Args default to: lang-server"
                ).apply { putClientProperty("html.disable", false) })
            })
            .addComponentFillVertically(JPanel(), 0)
            .panel
        return panel!!
    }

    override fun isModified(): Boolean {
        val settings = ApplicationManager.getApplication().getService(CxSettings::class.java)
        return mode != settings.serverMode ||
            serverPathField.text.trim() != settings.serverPath ||
            serverArgsField.text.trim() != settings.serverArgs ||
            localPathField.text.trim() != settings.localProjectPath
    }

    override fun apply() {
        val settings = ApplicationManager.getApplication().getService(CxSettings::class.java)
        settings.serverMode = mode
        settings.serverPath = serverPathField.text.trim()
        settings.serverArgs = serverArgsField.text.trim()
        settings.localProjectPath = localPathField.text.trim()
    }

    override fun reset() {
        val settings = ApplicationManager.getApplication().getService(CxSettings::class.java)
        if (settings.serverMode == CxSettings.MODE_INSTALLED) installedRadio.isSelected = true else localRadio.isSelected = true
        serverPathField.text = settings.serverPath
        serverArgsField.text = settings.serverArgs
        localPathField.text = settings.localProjectPath
    }

    private val mode: String
        get() = if (installedRadio.isSelected) CxSettings.MODE_INSTALLED else CxSettings.MODE_LOCAL
}
