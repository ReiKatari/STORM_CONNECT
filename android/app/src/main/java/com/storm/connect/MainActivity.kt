package com.storm.connect

import android.Manifest
import android.app.AlertDialog
import android.app.GameManager
import android.app.GameState
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.provider.Settings
import android.text.InputType
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.ScaleGestureDetector
import android.view.View
import android.view.ViewGroup
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.storm.connect.engine.DiscoveredLanDevice
import com.storm.connect.engine.LanDiscoveryManager
import com.storm.connect.engine.RemoteFileEntry
import com.storm.connect.engine.RemoteMonitor
import com.storm.connect.engine.RemoteSessionClient
import com.storm.connect.protocol.ProtocolConstants
import com.storm.connect.protocol.WakeOnLan
import com.storm.connect.ui.StormUiCatalog
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import kotlin.random.Random

class MainActivity : AppCompatActivity() {
    private lateinit var stormRootDir: File
    private lateinit var downloadsDir: File
    private lateinit var prefs: android.content.SharedPreferences

    private var currentLang = StormUiCatalog.detectSystemLanguage()
    private var currentThemeIdx = 0
    private var currentAccentIdx = 0
    private var currentFontIdx = 0
    private var myStormId = ""

    private lateinit var lanManager: LanDiscoveryManager
    private lateinit var sessionClient: RemoteSessionClient

    private lateinit var rootLayout: LinearLayout
    private lateinit var headerLayout: LinearLayout
    private lateinit var bodyContainer: LinearLayout
    private lateinit var navBar: LinearLayout

    private lateinit var tabDashboard: ScrollView
    private lateinit var tabViewer: LinearLayout
    private lateinit var tabFiles: LinearLayout
    private lateinit var tabSettings: ScrollView

    private lateinit var txtSubtitle: TextView
    private lateinit var txtMyIdValue: TextView
    private lateinit var txtStatus: TextView
    private lateinit var edtTarget: EditText
    private lateinit var edtRelayUrl: EditText
    private lateinit var lanListLayout: LinearLayout
    private lateinit var remoteImageView: ImageView
    private lateinit var txtViewerHud: TextView
    private lateinit var spinnerMonitors: Spinner
    private lateinit var fileListLayout: LinearLayout
    private lateinit var txtRemotePath: TextView

    private var currentMonitors: List<RemoteMonitor> = emptyList()
    private var currentRemotePath = ""
    private var zoomScale = 1f
    private lateinit var scaleDetector: ScaleGestureDetector

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        prefs = getSharedPreferences("storm_connect_prefs", MODE_PRIVATE)
        myStormId = prefs.getString("storm_id", null) ?: run {
            val id = "${Random.nextInt(100, 1000)}${Random.nextInt(100, 1000)}${Random.nextInt(100, 1000)}"
            prefs.edit().putString("storm_id", id).apply()
            id
        }
        currentLang = prefs.getString("lang", StormUiCatalog.detectSystemLanguage()) ?: "en"
        currentThemeIdx = prefs.getInt("theme_idx", 0)
        currentAccentIdx = prefs.getInt("accent_idx", 0)
        currentFontIdx = prefs.getInt("font_idx", 0)

        stormRootDir = File(Environment.getExternalStorageDirectory(), "STORM CONNECT")
        downloadsDir = File(stormRootDir, "downloads")

        ensureStormRootStorageAndPermissions()
        enablePhoneGameBoosterIntegration()

        lanManager = LanDiscoveryManager(myStormId)
        sessionClient = RemoteSessionClient(myStormId, downloadsDir)
        wireSessionCallbacks()

        buildUi()
        applyThemeAndLocale()

        lanManager.start { devices ->
            runOnUiThread { renderLanDevices(devices) }
        }
    }

    private fun ensureStormRootStorageAndPermissions() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            if (!Environment.isExternalStorageManager()) {
                val intent = Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:$packageName"))
                runCatching { startActivity(intent) }.onFailure {
                    runCatching { startActivity(Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION)) }
                }
            } else {
                initStormRootDirectory()
            }
        } else {
            val granted = ContextCompat.checkSelfPermission(this, Manifest.permission.WRITE_EXTERNAL_STORAGE) == PackageManager.PERMISSION_GRANTED
            if (!granted) {
                ActivityCompat.requestPermissions(this, arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE, Manifest.permission.WRITE_EXTERNAL_STORAGE), 1001)
            } else {
                initStormRootDirectory()
            }
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                ActivityCompat.requestPermissions(this, arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1002)
            }
        }
    }

    private fun initStormRootDirectory() {
        runCatching {
            listOf("settings", "downloads", "screenshots", "logs", "updates").forEach { sub ->
                File(stormRootDir, sub).mkdirs()
            }
        }
    }

    private fun enablePhoneGameBoosterIntegration() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            display?.supportedModes?.maxByOrNull { it.refreshRate }?.let { maxMode ->
                window.attributes = window.attributes.apply {
                    preferredDisplayModeId = maxMode.modeId
                }
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            runCatching {
                val gm = getSystemService(GameManager::class.java)
                gm?.setGameState(GameState(false, GameState.MODE_GAMEPLAY_UNINTERRUPTIBLE))
            }
        }
    }

    private fun buildUi() {
        rootLayout = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        // Header
        headerLayout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(36, 28, 36, 24)
        }
        val titleRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        val logo = ImageView(this).apply {
            setImageResource(R.mipmap.ic_launcher)
            layoutParams = LinearLayout.LayoutParams(96, 96).apply { marginEnd = 24 }
        }
        val titleCol = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val txtTitle = TextView(this).apply {
            text = "STORM CONNECT"
            textSize = 20f
            setTypeface(null, android.graphics.Typeface.BOLD)
        }
        txtSubtitle = TextView(this).apply { textSize = 12f }
        titleCol.addView(txtTitle)
        titleCol.addView(txtSubtitle)
        titleRow.addView(logo)
        titleRow.addView(titleCol)
        headerLayout.addView(titleRow)
        rootLayout.addView(headerLayout)

        // Body container
        bodyContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f)
        }

        // 1. Dashboard Tab
        tabDashboard = ScrollView(this)
        val dashStack = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(32, 24, 32, 24)
        }

        val idCard = createCard()
        val lblMyId = TextView(this).apply { text = StormUiCatalog.t(currentLang, "my_id"); textSize = 11f }
        txtMyIdValue = TextView(this).apply {
            text = "${myStormId.substring(0, 3)} ${myStormId.substring(3, 6)} ${myStormId.substring(6, 9)}"
            textSize = 28f
            setTypeface(null, android.graphics.Typeface.BOLD)
        }
        idCard.addView(lblMyId)
        idCard.addView(txtMyIdValue)
        dashStack.addView(idCard)

        val connCard = createCard()
        edtTarget = EditText(this).apply {
            hint = StormUiCatalog.t(currentLang, "target_hint")
            setPadding(24, 24, 24, 24)
        }
        val btnConnect = createVolumetricButton(StormUiCatalog.t(currentLang, "connect")) {
            it.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
            connectTo(edtTarget.text.toString())
        }
        txtStatus = TextView(this).apply {
            text = StormUiCatalog.t(currentLang, "storage_ok")
            textSize = 12f
            setPadding(0, 14, 0, 0)
        }
        connCard.addView(edtTarget)
        connCard.addView(btnConnect)
        connCard.addView(txtStatus)
        dashStack.addView(connCard)

        val lanCard = createCard()
        val lblLan = TextView(this).apply { text = StormUiCatalog.t(currentLang, "lan_title"); textSize = 12f }
        lanListLayout = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        lanCard.addView(lblLan)
        lanCard.addView(lanListLayout)
        dashStack.addView(lanCard)

        tabDashboard.addView(dashStack)
        bodyContainer.addView(tabDashboard)

        // 2. Remote Viewer Tab
        tabViewer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
            setPadding(16, 12, 16, 12)
        }
        val viewerBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        spinnerMonitors = Spinner(this).apply {
            layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onItemSelected(parent: AdapterView<*>?, view: View?, pos: Int, id: Long) {
                    currentMonitors.getOrNull(pos)?.let { sessionClient.selectMonitor(it.index) }
                }
                override fun onNothingSelected(parent: AdapterView<*>?) {}
            }
        }
        val btnDisc = createVolumetricButton(StormUiCatalog.t(currentLang, "disconnect")) {
            sessionClient.disconnect()
            switchTab(0)
        }
        viewerBar.addView(spinnerMonitors)
        viewerBar.addView(btnDisc)
        txtViewerHud = TextView(this).apply { text = "Idle"; textSize = 11.5f; setPadding(4, 6, 4, 8) }
        remoteImageView = ImageView(this).apply {
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f)
            scaleType = ImageView.ScaleType.FIT_CENTER
            setBackgroundColor(Color.BLACK)
        }
        scaleDetector = ScaleGestureDetector(this, object : ScaleGestureDetector.SimpleOnScaleGestureListener() {
            override fun onScale(detector: ScaleGestureDetector): Boolean {
                zoomScale = (zoomScale * detector.scaleFactor).coerceIn(1f, 4f)
                remoteImageView.scaleX = zoomScale
                remoteImageView.scaleY = zoomScale
                return true
            }
        })
        remoteImageView.setOnTouchListener { v, ev ->
            scaleDetector.onTouchEvent(ev)
            if (ev.pointerCount == 1 && v.width > 0 && v.height > 0) {
                val nx = (ev.x / v.width).toDouble().coerceIn(0.0, 1.0)
                val ny = (ev.y / v.height).toDouble().coerceIn(0.0, 1.0)
                when (ev.actionMasked) {
                    MotionEvent.ACTION_DOWN -> sessionClient.sendMouse(nx, ny, 1, 1)
                    MotionEvent.ACTION_MOVE -> sessionClient.sendMouse(nx, ny, 0, 0)
                    MotionEvent.ACTION_UP -> sessionClient.sendMouse(nx, ny, 1, 2)
                }
            }
            true
        }
        tabViewer.addView(viewerBar)
        tabViewer.addView(txtViewerHud)
        tabViewer.addView(remoteImageView)
        bodyContainer.addView(tabViewer)

        // 3. Files Tab
        tabFiles = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
            setPadding(24, 16, 24, 16)
        }
        txtRemotePath = TextView(this).apply { text = "/"; textSize = 13f; setPadding(0, 0, 0, 12) }
        val btnUpDir = createVolumetricButton("Up / Refresh") {
            sessionClient.requestFileList(currentRemotePath)
        }
        val fileScroll = ScrollView(this).apply {
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f)
        }
        fileListLayout = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        fileScroll.addView(fileListLayout)
        tabFiles.addView(txtRemotePath)
        tabFiles.addView(btnUpDir)
        tabFiles.addView(fileScroll)
        bodyContainer.addView(tabFiles)

        // 4. Settings Tab (11 Languages, 9 Themes, 13 Accents, 10 Fonts, Relay URL, Updates)
        tabSettings = ScrollView(this).apply { visibility = View.GONE }
        val setStack = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(32, 24, 32, 24)
        }
        setStack.addView(createSelectorRow("Language", StormUiCatalog.languages.map { it.second },
            StormUiCatalog.languages.indexOfFirst { it.first == currentLang }.coerceAtLeast(0)) { idx ->
            currentLang = StormUiCatalog.languages[idx].first
            prefs.edit().putString("lang", currentLang).apply()
            applyThemeAndLocale()
        })
        setStack.addView(createSelectorRow("Theme", StormUiCatalog.themes.map { it.name }, currentThemeIdx) { idx ->
            currentThemeIdx = idx
            prefs.edit().putInt("theme_idx", idx).apply()
            applyThemeAndLocale()
        })
        setStack.addView(createSelectorRow("Accent", StormUiCatalog.accents.map { it.name }, currentAccentIdx) { idx ->
            currentAccentIdx = idx
            prefs.edit().putInt("accent_idx", idx).apply()
            applyThemeAndLocale()
        })
        setStack.addView(createSelectorRow("Font", StormUiCatalog.fonts.map { it.name }, currentFontIdx) { idx ->
            currentFontIdx = idx
            prefs.edit().putInt("font_idx", idx).apply()
            applyThemeAndLocale()
        })
        edtRelayUrl = EditText(this).apply {
            setText(prefs.getString("relay_url", ProtocolConstants.DEFAULT_RELAY_URL))
        }
        setStack.addView(edtRelayUrl)
        setStack.addView(createVolumetricButton(StormUiCatalog.t(currentLang, "check_updates")) {
            checkAndroidUpdates()
        })
        tabSettings.addView(setStack)
        bodyContainer.addView(tabSettings)

        rootLayout.addView(bodyContainer)

        // Bottom Navigation Dock
        navBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(16, 12, 16, 18)
        }
        listOf("Connect", "Viewer", "Files", "Settings").forEachIndexed { idx, label ->
            val btn = Button(this).apply {
                text = label
                layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { marginEnd = 8 }
                setOnClickListener {
                    it.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                    switchTab(idx)
                }
            }
            navBar.addView(btn)
        }
        rootLayout.addView(navBar)
        setContentView(rootLayout)
    }

    private fun switchTab(idx: Int) {
        tabDashboard.visibility = if (idx == 0) View.VISIBLE else View.GONE
        tabViewer.visibility = if (idx == 1) View.VISIBLE else View.GONE
        tabFiles.visibility = if (idx == 2) View.VISIBLE else View.GONE
        tabSettings.visibility = if (idx == 3) View.VISIBLE else View.GONE
    }

    private fun applyThemeAndLocale() {
        val theme = StormUiCatalog.themes[currentThemeIdx.coerceIn(0, StormUiCatalog.themes.lastIndex)]
        val accent = StormUiCatalog.accents[currentAccentIdx.coerceIn(0, StormUiCatalog.accents.lastIndex)]
        val font = StormUiCatalog.fonts[currentFontIdx.coerceIn(0, StormUiCatalog.fonts.lastIndex)]

        rootLayout.setBackgroundColor(theme.bgPrimary)
        headerLayout.setBackgroundColor(theme.bgSecondary)
        navBar.setBackgroundColor(theme.bgSecondary)
        window.statusBarColor = theme.bgSecondary
        window.navigationBarColor = theme.bgPrimary

        txtSubtitle.text = StormUiCatalog.t(currentLang, "subtitle")
        txtSubtitle.setTextColor(theme.textSecondary)
        txtMyIdValue.setTextColor(accent.primary)
        txtStatus.setTextColor(theme.textSecondary)

        applyStyleRecursive(rootLayout, theme.textPrimary, theme.textSecondary, theme.cardBg, theme.cardBorder, accent.primary, font.typeface)
    }

    private fun applyStyleRecursive(v: View, fg: Int, sec: Int, cardBg: Int, border: Int, accent: Int, tf: android.graphics.Typeface) {
        when (v) {
            is EditText -> {
                v.setTextColor(fg)
                v.setHintTextColor(sec)
                v.typeface = tf
            }
            is Button -> {
                v.setTextColor(Color.WHITE)
                v.typeface = tf
                v.background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(accent, Color.parseColor("#06B6D4"))).apply {
                    cornerRadius = 22f
                }
            }
            is TextView -> {
                if (v !== txtMyIdValue && v !== txtSubtitle && v !== txtStatus) v.setTextColor(fg)
                v.typeface = tf
            }
            is ViewGroup -> {
                for (i in 0 until v.childCount) applyStyleRecursive(v.getChildAt(i), fg, sec, cardBg, border, accent, tf)
            }
        }
    }

    private fun createCard(): LinearLayout {
        val theme = StormUiCatalog.themes[currentThemeIdx]
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(28, 24, 28, 24)
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = 20 }
            background = GradientDrawable().apply {
                setColor(theme.cardBg)
                setStroke(2, theme.cardBorder)
                cornerRadius = 24f
            }
        }
    }

    private fun createVolumetricButton(label: String, onClick: (View) -> Unit): Button {
        return Button(this).apply {
            text = label
            isAllCaps = false
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = 14 }
            setOnClickListener(onClick)
        }
    }

    private fun createSelectorRow(title: String, items: List<String>, selected: Int, onPick: (Int) -> Unit): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = 16 }
            addView(TextView(this@MainActivity).apply { text = title; textSize = 12f })
            val sp = Spinner(this@MainActivity).apply {
                adapter = ArrayAdapter(this@MainActivity, android.R.layout.simple_spinner_dropdown_item, items)
                setSelection(selected, false)
                onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                    var init = true
                    override fun onItemSelected(p: AdapterView<*>?, v: View?, pos: Int, id: Long) {
                        if (init) { init = false; return }
                        onPick(pos)
                    }
                    override fun onNothingSelected(p: AdapterView<*>?) {}
                }
            }
            addView(sp)
        }
    }

    private fun wireSessionCallbacks() {
        sessionClient.onStatus = { msg -> runOnUiThread { txtStatus.text = msg } }
        sessionClient.onFrame = { bmp: Bitmap ->
            runOnUiThread {
                remoteImageView.setImageBitmap(bmp)
                if (tabViewer.visibility != View.VISIBLE) switchTab(1)
            }
        }
        sessionClient.onMonitors = { mons ->
            runOnUiThread {
                currentMonitors = mons
                spinnerMonitors.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_dropdown_item, mons.map { it.name })
            }
        }
        sessionClient.onTelemetry = { t ->
            runOnUiThread { txtViewerHud.text = "${t.mode} | ${t.width}x${t.height} | ${"%.1f".format(t.fps)} FPS | ${t.rttMs} ms | ${"%.2f".format(t.mbps)} Mbps" }
        }
        sessionClient.onFileList = { path, entries ->
            runOnUiThread {
                currentRemotePath = path
                txtRemotePath.text = path.ifBlank { "/" }
                fileListLayout.removeAllViews()
                entries.forEach { entry ->
                    val btn = Button(this).apply {
                        text = if (entry.isDirectory) "[DIR] ${entry.name}" else "${entry.name} (${entry.sizeBytes / 1024} KB)"
                        isAllCaps = false
                        setOnClickListener {
                            if (entry.isDirectory) sessionClient.requestFileList(entry.fullPath)
                            else sessionClient.requestDownload(entry.fullPath, entry.name)
                        }
                    }
                    fileListLayout.addView(btn)
                }
            }
        }
        sessionClient.onTransferStatus = { s -> runOnUiThread { Toast.makeText(this, s, Toast.LENGTH_SHORT).show() } }
        sessionClient.onDisconnected = { reason -> runOnUiThread { txtStatus.text = "Disconnected: $reason" } }
    }

    private fun renderLanDevices(devices: List<DiscoveredLanDevice>) {
        lanListLayout.removeAllViews()
        if (devices.isEmpty()) {
            lanListLayout.addView(TextView(this).apply { text = "Scanning LAN..."; textSize = 12f })
            return
        }
        devices.forEach { dev ->
            val btn = createVolumetricButton("${dev.hostName} (${dev.localIp}) - ${dev.osName}") {
                connectTo(dev.stormId)
            }
            lanListLayout.addView(btn)
        }
    }

    private fun connectTo(target: String) {
        if (target.isBlank()) return
        val relay = edtRelayUrl.text.toString().ifBlank { ProtocolConstants.DEFAULT_RELAY_URL }
        prefs.edit().putString("relay_url", relay).apply()
        sessionClient.connect(target, relay, lanManager.currentDevices()) { hostName ->
            val def = CompletableDeferred<String?>()
            runOnUiThread {
                val input = EditText(this).apply { inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD }
                AlertDialog.Builder(this)
                    .setTitle("STORM CONNECT - $hostName")
                    .setMessage("Enter PIN or Password (empty = request consent):")
                    .setView(input)
                    .setPositiveButton("OK") { _, _ -> def.complete(input.text.toString()) }
                    .setNegativeButton("Cancel") { _, _ -> def.complete(null) }
                    .setOnCancelListener { def.complete(null) }
                    .show()
            }
            def.await()
        }
    }

    private fun checkAndroidUpdates() {
        val installedVer = runCatching {
            packageManager.getPackageInfo(packageName, 0).versionName ?: "1.1.0"
        }.getOrDefault("1.1.0")
        CoroutineScope(Dispatchers.IO).launch {
            runCatching {
                val url = URL("https://stormgamesworld.ru/api/programs/releases?repo=STORM_CONNECT&latest=1")
                val conn = url.openConnection() as HttpURLConnection
                conn.connectTimeout = 5000
                conn.readTimeout = 5000
                val body = conn.inputStream.bufferedReader().readText()
                val json = JSONObject(body)
                val rel = json.optJSONObject("release") ?: json
                val latest = rel.optString("version", installedVer).removePrefix("v")
                withContext(Dispatchers.Main) {
                    Toast.makeText(this@MainActivity, "Installed: $installedVer | Latest: $latest", Toast.LENGTH_LONG).show()
                }
            }.onFailure {
                withContext(Dispatchers.Main) {
                    Toast.makeText(this@MainActivity, "Installed: $installedVer", Toast.LENGTH_SHORT).show()
                }
            }
        }
    }

    override fun onDestroy() {
        lanManager.stop()
        sessionClient.destroy()
        super.onDestroy()
    }
}
