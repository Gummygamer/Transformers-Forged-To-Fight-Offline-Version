package com.gummygamer.tftfpvphost

import android.Manifest
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.widget.doAfterTextChanged
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.gummygamer.tftfpvphost.databinding.ActivityMainBinding
import com.gummygamer.tftfpvphost.ui.HostInput
import com.gummygamer.tftfpvphost.ui.HostUiState
import com.gummygamer.tftfpvphost.ui.HostViewModel
import com.gummygamer.tftfpvphost.ui.MatchmakingText
import com.gummygamer.tftfpvphost.ui.ParseError
import com.gummygamer.tftfpvphost.ui.Parsed
import com.gummygamer.tftfpvphost.server.HostRuntime
import com.gummygamer.tftfpvphost.server.HostConfig
import kotlinx.coroutines.launch

/** Host screen: start/stop, the address to hand players, live matchmaking state and the server log. */
class MainActivity : AppCompatActivity() {
    private lateinit var binding: ActivityMainBinding
    private val viewModel: HostViewModel by lazy { ViewModelProvider(this)[HostViewModel::class.java] }
    private var latest = HostUiState()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)
        requestNotificationPermissionIfNeeded()
        binding.portInput.setText(viewModel.portText)
        binding.ttlInput.setText(viewModel.ttlMsText)
        binding.cdnSwitch.isChecked = viewModel.rewriteCdn
        binding.portInput.doAfterTextChanged { onPortEdited(it?.toString().orEmpty()) }
        binding.ttlInput.doAfterTextChanged { onTtlEdited(it?.toString().orEmpty()) }
        binding.cdnSwitch.setOnCheckedChangeListener { _, checked -> viewModel.rewriteCdn = checked }
        binding.toggleButton.setOnClickListener { toggle() }
        binding.copyButton.setOnClickListener { copyBuildFlags() }
        binding.resetButton.setOnClickListener { confirmReset() }
        binding.clearLogButton.setOnClickListener { viewModel.clearLog() }
        binding.copyLogButton.setOnClickListener { copyLog() }
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                viewModel.state.collect { render(it) }
            }
        }
    }

    private fun onPortEdited(text: String) {
        viewModel.portText = text
        binding.portLayout.error = (HostInput.port(text) as? Parsed.Bad)?.let { message(it.error) }
    }

    private fun onTtlEdited(text: String) {
        viewModel.ttlMsText = text
        val parsed = HostInput.ttlMs(text)
        binding.ttlLayout.error = (parsed as? Parsed.Bad)?.let { message(it.error) }
        if (parsed is Parsed.Ok && latest.running) viewModel.applyTtl(parsed.value)
    }

    private fun message(error: ParseError): String = getString(
        when (error) {
            ParseError.PORT_NOT_NUMBER -> R.string.error_port_not_number
            ParseError.PORT_RANGE -> R.string.error_port_range
            ParseError.TTL_NOT_NUMBER -> R.string.error_ttl_not_number
            ParseError.TTL_RANGE -> R.string.error_ttl_range
        })

    private fun toggle() {
        val intent = Intent(this, HostService::class.java)
        if (latest.running) {
            intent.action = HostService.ACTION_STOP
            startService(intent)
            return
        }
        val port = HostInput.port(binding.portInput.text.toString())
        val ttl = HostInput.ttlMs(binding.ttlInput.text.toString())
        if (port is Parsed.Bad || ttl is Parsed.Bad) {
            onPortEdited(binding.portInput.text.toString())
            onTtlEdited(binding.ttlInput.text.toString())
            return
        }
        if (latest.addresses.isEmpty()) return
        intent.putExtra(HostService.EXTRA_PORT, (port as Parsed.Ok).value)
        intent.putExtra(HostService.EXTRA_TTL_MS, (ttl as Parsed.Ok).value)
        intent.putExtra(HostService.EXTRA_HOST, latest.addresses.firstOrNull().orEmpty())
        intent.putExtra(HostService.EXTRA_REWRITE_CDN, binding.cdnSwitch.isChecked)
        ContextCompat.startForegroundService(this, intent)
    }

    private fun copyBuildFlags() {
        val address = latest.addresses.firstOrNull() ?: return
        val port = (HostInput.port(binding.portInput.text.toString()) as? Parsed.Ok)?.value
            ?: HostConfig.DEFAULT_PORT
        val flags = getString(R.string.address_flags, address, port)
        getSystemService(ClipboardManager::class.java)
            .setPrimaryClip(ClipData.newPlainText(getString(R.string.card_address), flags))
        Toast.makeText(this, R.string.command_copied, Toast.LENGTH_SHORT).show()
    }

    private fun copyAddress(address: String) {
        getSystemService(ClipboardManager::class.java)
            .setPrimaryClip(ClipData.newPlainText(getString(R.string.card_address), address))
        Toast.makeText(this, getString(R.string.address_copied, address), Toast.LENGTH_SHORT).show()
    }

    private fun copyLog() {
        val text = latest.log.joinToString("\n")
        getSystemService(ClipboardManager::class.java)
            .setPrimaryClip(ClipData.newPlainText(getString(R.string.card_log), text))
        Toast.makeText(this, R.string.log_copied, Toast.LENGTH_SHORT).show()
    }

    private fun confirmReset() {
        AlertDialog.Builder(this)
            .setTitle(R.string.reset_title)
            .setMessage(R.string.reset_message)
            .setPositiveButton(R.string.reset_confirm) { _, _ -> doReset() }
            .setNegativeButton(R.string.reset_cancel, null)
            .show()
    }

    private fun doReset() {
        val done = viewModel.resetState()
        Toast.makeText(this, if (done) R.string.reset_done else R.string.reset_failed, Toast.LENGTH_SHORT).show()
    }

    private fun render(state: HostUiState) {
        latest = state
        binding.toggleButton.setText(if (state.running) R.string.action_stop else R.string.action_start)
        binding.statusText.text =
            if (state.running) getString(
                R.string.status_running, state.addresses.firstOrNull() ?: "0.0.0.0", state.port)
            else getString(R.string.status_stopped)
        binding.errorText.text = state.error
        binding.errorText.visibility = if (state.error != null && !state.running) android.view.View.VISIBLE else android.view.View.GONE
        binding.portInput.isEnabled = !state.running
        binding.cdnSwitch.isEnabled = !state.running
        binding.toggleButton.isEnabled = state.running || state.addresses.isNotEmpty()
        renderAddress(state)
        binding.resetButton.isEnabled = state.running
        binding.peersText.text = MatchmakingText.peers(this, state.status)
        binding.matchesText.text = MatchmakingText.matches(this, state.status)
        binding.resultsText.text = MatchmakingText.results(this, state.status)
        renderLog(state.log)
    }

    private fun renderAddress(state: HostUiState) {
        val first = state.addresses.firstOrNull()
        binding.copyButton.isEnabled = first != null
        binding.addressText.text = first ?: getString(R.string.address_none)
        val port = if (state.running) state.port else HostInput.port(binding.portInput.text.toString()).let { (it as? Parsed.Ok)?.value }
        val help = if (first == null) getString(R.string.address_help_none)
        else getString(R.string.address_help_ready)
        binding.addressHelp.text = help
        binding.addressList.removeAllViews()
        state.addresses.forEach { address ->
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            row.addView(TextView(this).apply {
                text = address
                textSize = 16f
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            })
            row.addView(com.google.android.material.button.MaterialButton(this).apply {
                setText(R.string.action_copy_ip)
                setOnClickListener { copyAddress(address) }
            })
            binding.addressList.addView(row)
        }
        val commandPort = port ?: HostConfig.DEFAULT_PORT
        if (first != null) {
            binding.commandText.text = getString(R.string.address_flags, first, commandPort)
            binding.fullCommandText.text = getString(R.string.address_command, first, commandPort)
            binding.patcherHelp.text = getString(R.string.address_patcher, first, commandPort)
        } else {
            binding.commandText.text = ""
            binding.fullCommandText.text = ""
            binding.patcherHelp.text = ""
        }
    }

    private fun renderLog(lines: List<String>) {
        val text = lines.joinToString("\n")
        if (binding.logText.text.toString() == text) return
        binding.logText.text = text
        binding.logScroll.post { binding.logScroll.fullScroll(android.view.View.FOCUS_DOWN) }
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), NOTIFICATION_REQUEST)
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == NOTIFICATION_REQUEST &&
            (grantResults.isEmpty() || grantResults[0] != PackageManager.PERMISSION_GRANTED)) {
            HostRuntime.log.append(getString(R.string.notification_permission_denied))
        }
    }

    private companion object {
        const val NOTIFICATION_REQUEST = 41
    }
}
