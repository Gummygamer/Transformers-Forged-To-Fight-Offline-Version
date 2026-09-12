package com.gummygamer.apkpatcher

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.text.method.ScrollingMovementMethod
import android.view.View
import android.widget.ArrayAdapter
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.isVisible
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.google.android.material.button.MaterialButton
import com.google.android.material.checkbox.MaterialCheckBox
import com.google.android.material.radiobutton.MaterialRadioButton
import com.google.android.material.textfield.TextInputEditText
import com.google.android.material.textfield.TextInputLayout
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var viewModel: PatchViewModel

    // Source APK
    private lateinit var btnSelectSource: MaterialButton
    private lateinit var txtSourceName: TextView

    // Architecture
    private lateinit var radioArm64: MaterialRadioButton
    private lateinit var radioArmv7: MaterialRadioButton
    private lateinit var chkKeepOther: MaterialCheckBox
    private lateinit var txtArmv7Warning: TextView

    // Server
    private lateinit var radioBundled: MaterialRadioButton
    private lateinit var radioSeparate: MaterialRadioButton
    private lateinit var txtBundledNote: TextView
    private lateinit var tilHost: TextInputLayout
    private lateinit var edtHost: TextInputEditText
    private lateinit var tilPort: TextInputLayout
    private lateinit var edtPort: TextInputEditText
    private lateinit var dropScheme: android.widget.AutoCompleteTextView

    // libil2cpp
    private lateinit var chkAutoPatch: MaterialCheckBox
    private lateinit var btnSelectIl2cpp: MaterialButton
    private lateinit var txtIl2cppName: TextView

    // Signing
    private lateinit var btnSelectKeystore: MaterialButton
    private lateinit var txtKeystoreName: TextView
    private lateinit var edtKsPass: TextInputEditText
    private lateinit var edtKeyPass: TextInputEditText
    private lateinit var chkInstall: MaterialCheckBox

    // Validation
    private lateinit var cardValidation: View
    private lateinit var validationContainer: android.view.ViewGroup

    // Preview + Log
    private lateinit var cardPreview: View
    private lateinit var txtStepPreview: TextView
    private lateinit var txtResult: TextView
    private lateinit var cardLog: View
    private lateinit var txtLog: TextView
    private lateinit var logScroll: android.widget.ScrollView

    // Buttons
    private lateinit var btnBuild: MaterialButton
    private lateinit var btnCancel: MaterialButton
    private lateinit var btnExport: MaterialButton

    // SAF launchers
    private val selectSourceLauncher = registerForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            // Take persistent read permission
            contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
            val name = queryDisplayName(uri) ?: "unknown.apk"
            viewModel.setSourceApk(uri.toString(), name)
        }
    }

    private val selectIl2cppLauncher = registerForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
            val name = queryDisplayName(uri) ?: "libil2cpp.so"
            viewModel.setPatchedIl2cpp(uri.toString(), name)
        }
    }

    private val selectKeystoreLauncher = registerForActivityResult(
        ActivityResultContracts.OpenDocument()
    ) { uri: Uri? ->
        if (uri != null) {
            contentResolver.takePersistableUriPermission(
                uri, Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
            val name = queryDisplayName(uri) ?: "keystore.jks"
            viewModel.setKeystore(uri.toString(), name)
        }
    }

    private val exportLauncher = registerForActivityResult(
        ActivityResultContracts.CreateDocument("application/vnd.android.package-archive")
    ) { uri: Uri? ->
        if (uri != null) {
            viewModel.copyToUri(uri)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        viewModel = androidx.lifecycle.ViewModelProvider(this)[PatchViewModel::class.java]

        bindViews()
        setupListeners()
        observeState()
    }

    private fun bindViews() {
        btnSelectSource = findViewById(R.id.btnSelectSource)
        txtSourceName = findViewById(R.id.txtSourceName)
        radioArm64 = findViewById(R.id.radioArm64)
        radioArmv7 = findViewById(R.id.radioArmv7)
        chkKeepOther = findViewById(R.id.chkKeepOther)
        txtArmv7Warning = findViewById(R.id.txtArmv7Warning)
        radioBundled = findViewById(R.id.radioBundled)
        radioSeparate = findViewById(R.id.radioSeparate)
        txtBundledNote = findViewById(R.id.txtBundledNote)
        tilHost = findViewById(R.id.tilHost)
        edtHost = findViewById(R.id.edtHost)
        tilPort = findViewById(R.id.tilPort)
        edtPort = findViewById(R.id.edtPort)
        dropScheme = findViewById(R.id.dropScheme)
        chkAutoPatch = findViewById(R.id.chkAutoPatch)
        btnSelectIl2cpp = findViewById(R.id.btnSelectIl2cpp)
        txtIl2cppName = findViewById(R.id.txtIl2cppName)
        btnSelectKeystore = findViewById(R.id.btnSelectKeystore)
        txtKeystoreName = findViewById(R.id.txtKeystoreName)
        edtKsPass = findViewById(R.id.edtKsPass)
        edtKeyPass = findViewById(R.id.edtKeyPass)
        chkInstall = findViewById(R.id.chkInstall)
        cardValidation = findViewById(R.id.cardValidation)
        validationContainer = findViewById(R.id.validationContainer)
        cardPreview = findViewById(R.id.cardPreview)
        txtStepPreview = findViewById(R.id.txtStepPreview)
        txtResult = findViewById(R.id.txtResult)
        cardLog = findViewById(R.id.cardLog)
        txtLog = findViewById(R.id.txtLog)
        logScroll = findViewById(R.id.logScroll)
        btnBuild = findViewById(R.id.btnBuild)
        btnCancel = findViewById(R.id.btnCancel)
        btnExport = findViewById(R.id.btnExport)

        txtLog.movementMethod = ScrollingMovementMethod()

        // Scheme dropdown
        val schemes = arrayOf("http", "https")
        dropScheme.setAdapter(ArrayAdapter(this, android.R.layout.simple_dropdown_item_1line, schemes))
    }

    private fun setupListeners() {
        btnSelectSource.setOnClickListener {
            selectSourceLauncher.launch(arrayOf("application/vnd.android.package-archive", "application/octet-stream"))
        }

        radioArm64.setOnCheckedChangeListener { _, checked ->
            if (checked) viewModel.setAbi(PatchRequest.ARM64)
        }
        radioArmv7.setOnCheckedChangeListener { _, checked ->
            if (checked) viewModel.setAbi(PatchRequest.ARMV7)
        }

        chkKeepOther.setOnCheckedChangeListener { _, checked ->
            viewModel.setKeepOtherAbi(checked)
        }

        radioBundled.setOnCheckedChangeListener { _, checked ->
            if (checked) viewModel.setServerMode(PatchRequest.BUNDLED)
        }
        radioSeparate.setOnCheckedChangeListener { _, checked ->
            if (checked) viewModel.setServerMode(PatchRequest.SEPARATE)
        }

        // Host/port/scheme text watchers
        edtHost.setOnFocusChangeListener { _, _ -> syncHost() }
        edtPort.setOnFocusChangeListener { _, _ -> syncPort() }
        dropScheme.setOnItemClickListener { _, _, _, _ -> syncScheme() }

        chkAutoPatch.setOnCheckedChangeListener { _, checked ->
            viewModel.setAutoPatchIl2cpp(checked)
        }

        btnSelectIl2cpp.setOnClickListener {
            selectIl2cppLauncher.launch(arrayOf("application/octet-stream", "application/x-sharedlib"))
        }

        btnSelectKeystore.setOnClickListener {
            selectKeystoreLauncher.launch(arrayOf("application/octet-stream", "application/x-java-keystore"))
        }

        edtKsPass.setOnFocusChangeListener { _, _ -> syncPasswords() }
        edtKeyPass.setOnFocusChangeListener { _, _ -> syncPasswords() }

        chkInstall.setOnCheckedChangeListener { _, checked ->
            viewModel.setOfferInstall(checked)
        }

        btnBuild.setOnClickListener {
            syncAllFields()
            viewModel.startPatch()
        }

        btnCancel.setOnClickListener {
            viewModel.cancelPatch()
        }

        btnExport.setOnClickListener {
            exportLauncher.launch("patched-transformers.apk")
        }
    }

    private fun observeState() {
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                viewModel.uiState.collect { state ->
                    renderState(state)
                }
            }
        }
    }

    private fun renderState(s: UiState) {
        // Source APK
        txtSourceName.text = if (s.sourceApkName.isNotBlank()) s.sourceApkName else "No APK selected"

        // Architecture
        if (s.abi == PatchRequest.ARM64 && !radioArm64.isChecked) {
            radioArm64.isChecked = true
        } else if (s.abi == PatchRequest.ARMV7 && !radioArmv7.isChecked) {
            radioArmv7.isChecked = true
        }
        chkKeepOther.isChecked = s.keepOtherAbi

        // Armv7 warning
        val showArmv7Warn = s.abi == PatchRequest.ARMV7 && s.patchedIl2cppUri.isBlank() && !s.autoPatchIl2cpp
        txtArmv7Warning.isVisible = showArmv7Warn

        // Server mode
        val isBundled = s.serverMode == PatchRequest.BUNDLED
        if (isBundled && !radioBundled.isChecked) radioBundled.isChecked = true
        else if (!isBundled && !radioSeparate.isChecked) radioSeparate.isChecked = true

        txtBundledNote.isVisible = isBundled
        tilHost.isEnabled = !isBundled
        tilPort.isEnabled = !isBundled
        dropScheme.isEnabled = !isBundled
        if (edtHost.text.toString() != s.serverHost) edtHost.setText(s.serverHost)
        if (edtPort.text.toString() != s.serverPort.toString()) edtPort.setText(s.serverPort.toString())
        if (dropScheme.text.toString() != s.scheme) dropScheme.setText(s.scheme, false)

        // libil2cpp
        chkAutoPatch.isChecked = s.autoPatchIl2cpp
        txtIl2cppName.text = when {
            s.patchedIl2cppName.isNotBlank() -> s.patchedIl2cppName
            s.autoPatchIl2cpp -> "(auto-patch from source)"
            else -> "(no patching — hook may not run)"
        }

        // Signing
        txtKeystoreName.text = if (s.keystoreName.isNotBlank()) s.keystoreName else "(uses generated on-device keystore)"
        chkInstall.isChecked = s.offerInstall

        // Validation
        renderValidation(s.validationErrors, s.validationWarnings)

        // Step preview
        if (s.stepPreview.isNotEmpty()) {
            cardPreview.isVisible = true
            txtStepPreview.text = s.stepPreview.joinToString("\n")
        } else {
            cardPreview.isVisible = false
        }

        // Run result
        if (s.resultMessage.isNotBlank()) {
            txtResult.isVisible = true
            txtResult.text = s.resultMessage
            when (s.engineState) {
                PatcherState.SUCCEEDED -> txtResult.setTextColor(getColor(android.R.color.holo_green_dark))
                PatcherState.FAILED -> txtResult.setTextColor(getColor(android.R.color.holo_red_dark))
                PatcherState.CANCELLED -> txtResult.setTextColor(getColor(android.R.color.holo_orange_dark))
                else -> txtResult.setTextColor(getColor(android.R.color.darker_gray))
            }
        } else {
            txtResult.isVisible = false
        }

        // Build log
        if (s.logLines.isNotEmpty()) {
            cardLog.isVisible = true
            val logText = s.logLines.joinToString("\n") { line ->
                if (line.isError) "⚠ ${line.text}" else line.text
            }
            txtLog.text = logText
            logScroll.post { logScroll.fullScroll(android.view.View.FOCUS_DOWN) }
        } else if (s.engineState != PatcherState.RUNNING) {
            cardLog.isVisible = false
        }

        // Buttons
        val isRunning = s.engineState == PatcherState.RUNNING
        btnBuild.isEnabled = !isRunning && s.validationErrors.isEmpty() && s.sourceApkUri.isNotBlank()
        btnCancel.isEnabled = isRunning
        btnExport.isVisible = s.engineState == PatcherState.SUCCEEDED && s.outputFilePath.isNotBlank()

        // Install result
        if (s.installResult.isNotBlank() && s.engineState == PatcherState.SUCCEEDED) {
            txtResult.text = "${s.resultMessage}\n${s.installResult}"
        }
    }

    private fun renderValidation(errors: List<String>, warnings: List<String>) {
        validationContainer.removeViews(1, validationContainer.childCount - 1) // keep title

        if (errors.isEmpty() && warnings.isEmpty()) {
            cardValidation.isVisible = false
            return
        }

        cardValidation.isVisible = true

        for (error in errors) {
            val tv = TextView(this).apply {
                text = error
                setTextColor(0xFF701016.toInt())
                setBackgroundResource(R.drawable.error_background)
                setPadding(16, 10, 16, 10)
                textSize = 14f
            }
            val params = android.widget.LinearLayout.LayoutParams(
                android.widget.LinearLayout.LayoutParams.MATCH_PARENT,
                android.widget.LinearLayout.LayoutParams.WRAP_CONTENT
            )
            params.topMargin = 6
            validationContainer.addView(tv, params)
        }

        for (warning in warnings) {
            val tv = TextView(this).apply {
                text = warning
                setTextColor(0xFF6a4700.toInt())
                setBackgroundResource(R.drawable.warning_background)
                setPadding(16, 10, 16, 10)
                textSize = 14f
            }
            val params = android.widget.LinearLayout.LayoutParams(
                android.widget.LinearLayout.LayoutParams.MATCH_PARENT,
                android.widget.LinearLayout.LayoutParams.WRAP_CONTENT
            )
            params.topMargin = 6
            validationContainer.addView(tv, params)
        }

        if (errors.isEmpty() && warnings.size == 1 && warnings[0] == "Select a source APK to begin.") {
            // Don't show full card for the initial prompt
            cardValidation.isVisible = false
            txtSourceName.text = "Select a source APK to begin."
        }
    }

    private fun syncHost() {
        viewModel.setServerHost(edtHost.text?.toString() ?: "")
    }

    private fun syncPort() {
        val port = edtPort.text?.toString()?.toIntOrNull() ?: 8080
        viewModel.setServerPort(port)
    }

    private fun syncScheme() {
        viewModel.setScheme(dropScheme.text.toString())
    }

    private fun syncPasswords() {
        viewModel.setKeystorePassword(edtKsPass.text?.toString() ?: "")
        viewModel.setKeyPassword(edtKeyPass.text?.toString() ?: "")
    }

    private fun syncAllFields() {
        syncHost()
        syncPort()
        syncScheme()
        syncPasswords()
    }

    private fun queryDisplayName(uri: Uri): String? {
        var name: String? = null
        contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val idx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) name = cursor.getString(idx)
            }
        }
        return name
    }
}