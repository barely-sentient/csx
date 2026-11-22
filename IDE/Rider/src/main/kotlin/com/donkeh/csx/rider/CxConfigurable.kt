package com.donkeh.csx.rider

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
    private val installedRadio = JBRadioButton("Installed tool (csx on PATH)")
    private val serverPathField = TextFieldWithBrowseButton()
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
            .addLabeledComponent("Local CSX path:", localPathField)
            .addComponentFillVertically(JPanel(), 0)
            .panel
        return panel!!
    }

    override fun isModified(): Boolean {
        val settings = project.getService(CxSettings::class.java)
        return mode != settings.serverMode ||
            serverPathField.text.trim() != settings.serverPath ||
            localPathField.text.trim() != settings.localProjectPath
    }

    override fun apply() {
        val settings = project.getService(CxSettings::class.java)
        settings.serverMode = mode
        settings.serverPath = serverPathField.text.trim()
        settings.localProjectPath = localPathField.text.trim()
    }

    override fun reset() {
        val settings = project.getService(CxSettings::class.java)
        if (settings.serverMode == CxSettings.MODE_INSTALLED) installedRadio.isSelected = true else localRadio.isSelected = true
        serverPathField.text = settings.serverPath
        localPathField.text = settings.localProjectPath
    }

    private val mode: String
        get() = if (installedRadio.isSelected) CxSettings.MODE_INSTALLED else CxSettings.MODE_LOCAL
}
