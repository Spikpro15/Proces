package com.pcmonitor.usb

import android.app.AlertDialog
import android.content.pm.ActivityInfo
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Outline
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.TypedValue
import android.view.GestureDetector
import android.view.Gravity
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewOutlineProvider
import android.view.WindowManager
import android.widget.GridLayout
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.TimeUnit
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.roundToInt

class MainActivity : AppCompatActivity() {

    private val wsUrl = "ws://127.0.0.1:8765/ws"
    private val configUrl = "http://127.0.0.1:8765/api/config"
    private val mediaUrl = "http://127.0.0.1:8765/api/media"
    private val mediaArtUrl = "http://127.0.0.1:8765/api/media/art"
    private val macroUrl = "http://127.0.0.1:8765/api/macro"

    private val client = OkHttpClient.Builder()
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(10, TimeUnit.SECONDS)
        .build()

    private val mainHandler = Handler(Looper.getMainLooper())
    private var webSocket: WebSocket? = null
    private var isDestroyedFlag = false
    private var isDimmed = false
    private var orientationMode = 0
    private var isMediaOpen = false
    private var isMacroOpen = false
    private var lastArtVersion = -1
    private var lastMediaPlaying: Boolean? = null

    private var currentTheme = "midnight"
    private var currentFontScale = 1.0f
    private var mediaAccentColor = Color.parseColor("#58A6FF")

    private data class ThemeColors(val bg: Int, val card: Int, val border: Int, val accent: Int)
    private val themes = mapOf(
        "midnight" to ThemeColors(Color.parseColor("#090C10"), Color.parseColor("#131922"), Color.parseColor("#243042"), Color.parseColor("#58A6FF")),
        "amoled" to ThemeColors(Color.parseColor("#000000"), Color.parseColor("#0A0A0A"), Color.parseColor("#1F242C"), Color.parseColor("#00E676")),
        "spide" to ThemeColors(Color.parseColor("#14100E"), Color.parseColor("#211A16"), Color.parseColor("#3A2D26"), Color.parseColor("#FF8A50")),
        "cyber" to ThemeColors(Color.parseColor("#0B0814"), Color.parseColor("#161026"), Color.parseColor("#352459"), Color.parseColor("#D946EF"))
    )

    private lateinit var gestureDetector: GestureDetector
    private lateinit var rootContainer: View
    private lateinit var tvStatus: TextView
    private lateinit var tvGameName: TextView
    private lateinit var metricsGrid: GridLayout
    private lateinit var tvMiniMedia: TextView
    private lateinit var mediaOverlay: View
    private lateinit var ivMediaBackdrop: ImageView
    private lateinit var ivMediaArt: ImageView
    private lateinit var tvMediaStatus: TextView
    private lateinit var tvMediaTitle: TextView
    private lateinit var tvMediaArtist: TextView
    private lateinit var audioVisualizer: AudioVisualizerView
    private lateinit var mediaProgress: ProgressBar
    private lateinit var tvMediaPos: TextView
    private lateinit var tvMediaDur: TextView
    private lateinit var btnPlayPause: ImageView
    private lateinit var macroOverlay: View
    private lateinit var macroGrid: GridLayout

    private val allMetricIds = listOf(
        "fps", "cpu_load", "cpu_temp", "gpu_load", "gpu_temp", "ram_load",
        "gpu_vram", "cpu_power", "gpu_power", "cpu_clock", "gpu_clock"
    )
    private val metricLabels = mapOf(
        "fps" to "FPS в игре",
        "cpu_load" to "Загрузка CPU",
        "cpu_temp" to "Темп. CPU",
        "gpu_load" to "Загрузка GPU",
        "gpu_temp" to "Темп. GPU",
        "ram_load" to "Память RAM",
        "gpu_vram" to "Видеопамять",
        "cpu_power" to "Питание CPU",
        "gpu_power" to "Питание GPU",
        "cpu_clock" to "Частота CPU",
        "gpu_clock" to "Частота GPU"
    )

    private var enabledMetrics: MutableList<String> = mutableListOf(
        "fps", "cpu_load", "cpu_temp", "gpu_load", "gpu_temp", "ram_load"
    )

    private data class MacroItem(val label: String, val hotkey: String)
    private var macros: List<MacroItem> = listOf(
        MacroItem("🎙 Мут мик.", "Ctrl+Shift+M"),
        MacroItem("🔴 Запись", "Ctrl+Shift+R"),
        MacroItem("🎬 Сцена 1", "Ctrl+Shift+1"),
        MacroItem("🎬 Сцена 2", "Ctrl+Shift+2"),
        MacroItem("📸 Скриншот", "Win+PrintScreen"),
        MacroItem("🔇 Звук ПК", "vol_mute")
    )

    private data class CardHolder(
        val cardRoot: View,
        val tvLabel: TextView,
        val tvValue: TextView,
        val tvUnit: TextView,
        val tvSub: TextView,
        val sparkline: SparklineView,
        val progress: ProgressBar
    )

    private val cards = mutableMapOf<String, CardHolder>()
    private var lastPayload: JSONObject? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)

        rootContainer = findViewById(R.id.rootContainer)
        tvStatus = findViewById(R.id.tvStatus)
        tvGameName = findViewById(R.id.tvGameName)
        metricsGrid = findViewById(R.id.metricsGrid)
        tvMiniMedia = findViewById(R.id.tvMiniMedia)
        mediaOverlay = findViewById(R.id.mediaOverlay)
        ivMediaBackdrop = findViewById(R.id.ivMediaBackdrop)
        ivMediaArt = findViewById(R.id.ivMediaArt)
        tvMediaStatus = findViewById(R.id.tvMediaStatus)
        tvMediaTitle = findViewById(R.id.tvMediaTitle)
        tvMediaArtist = findViewById(R.id.tvMediaArtist)
        audioVisualizer = findViewById(R.id.audioVisualizer)
        mediaProgress = findViewById(R.id.mediaProgress)
        tvMediaPos = findViewById(R.id.tvMediaPos)
        tvMediaDur = findViewById(R.id.tvMediaDur)
        btnPlayPause = findViewById(R.id.btnPlayPause)
        macroOverlay = findViewById(R.id.macroOverlay)
        macroGrid = findViewById(R.id.macroGrid)

        // Crisp 22dp rounded corners on album art
        ivMediaArt.clipToOutline = true
        ivMediaArt.outlineProvider = object : ViewOutlineProvider() {
            override fun getOutline(view: View, outline: Outline) {
                outline.setRoundRect(0, 0, view.width, view.height, 40f)
            }
        }

        findViewById<View>(R.id.btnSettings).setOnClickListener { openSettingsDialog() }
        findViewById<View>(R.id.btnRotate).setOnClickListener { cycleOrientation() }
        findViewById<View>(R.id.btnDim).setOnClickListener { toggleDim() }
        findViewById<View>(R.id.btnMacros).setOnClickListener { showMacroOverlay(true) }
        findViewById<View>(R.id.btnCloseMacros).setOnClickListener { showMacroOverlay(false) }
        tvMiniMedia.setOnClickListener { showMediaOverlay(true) }
        findViewById<View>(R.id.btnCloseMedia).setOnClickListener { showMediaOverlay(false) }

        setupBouncyMediaBtn(findViewById(R.id.btnPrev), "prev")
        setupBouncyMediaBtn(btnPlayPause, "play_pause")
        setupBouncyMediaBtn(findViewById(R.id.btnNext), "next")
        setupBouncyMediaBtn(findViewById(R.id.btnVolDown), "vol_down")
        setupBouncyMediaBtn(findViewById(R.id.btnVolMute), "vol_mute")
        setupBouncyMediaBtn(findViewById(R.id.btnVolUp), "vol_up")

        gestureDetector = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onFling(e1: MotionEvent?, e2: MotionEvent, velocityX: Float, velocityY: Float): Boolean {
                if (e1 == null) return false
                val dx = e2.x - e1.x
                val dy = e2.y - e1.y
                if (abs(dy) > abs(dx) && abs(dy) > 90 && abs(velocityY) > 120) {
                    if (dy < 0 && !isMediaOpen && !isMacroOpen) { showMediaOverlay(true); return true }
                    if (dy > 0 && isMediaOpen) { showMediaOverlay(false); return true }
                } else if (abs(dx) > abs(dy) && abs(dx) > 90 && abs(velocityX) > 120) {
                    if (dx < 0 && !isMacroOpen && !isMediaOpen) { showMacroOverlay(true); return true }
                    if (dx > 0 && isMacroOpen) { showMacroOverlay(false); return true }
                }
                return false
            }
        })

        loadSavedMetrics()
        rebuildGrid()
        rebuildMacroGrid()
        applyTheme()
        applyMediaAccent(mediaAccentColor)
        applyImmersiveMode()
        connectWebSocket()
    }

    private fun setupBouncyMediaBtn(view: View, action: String) {
        view.setOnClickListener { v ->
            v.animate().scaleX(0.88f).scaleY(0.88f).setDuration(75).withEndAction {
                v.animate().scaleX(1f).scaleY(1f).setDuration(110).start()
            }.start()
            sendPost(mediaUrl, JSONObject().put("action", action))
        }
    }

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        gestureDetector.onTouchEvent(ev)
        return super.dispatchTouchEvent(ev)
    }

    private fun showMediaOverlay(show: Boolean) {
        if (show == isMediaOpen) return
        isMediaOpen = show
        val h = window.decorView.height.toFloat().coerceAtLeast(800f)
        if (show) {
            mediaOverlay.visibility = View.VISIBLE
            mediaOverlay.translationY = h
            mediaOverlay.animate().translationY(0f).setDuration(240).start()
        } else {
            mediaOverlay.animate().translationY(h).setDuration(200).withEndAction {
                mediaOverlay.visibility = View.GONE
            }.start()
        }
    }

    private fun showMacroOverlay(show: Boolean) {
        if (show == isMacroOpen) return
        isMacroOpen = show
        val w = window.decorView.width.toFloat().coerceAtLeast(800f)
        if (show) {
            macroOverlay.visibility = View.VISIBLE
            macroOverlay.translationX = w
            macroOverlay.animate().translationX(0f).setDuration(220).start()
        } else {
            macroOverlay.animate().translationX(w).setDuration(200).withEndAction {
                macroOverlay.visibility = View.GONE
            }.start()
        }
    }

    private fun sendPost(url: String, json: JSONObject) {
        val req = Request.Builder()
            .url(url)
            .post(json.toString().toRequestBody("application/json; charset=utf-8".toMediaType()))
            .build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) {}
            override fun onResponse(call: okhttp3.Call, response: Response) { response.close() }
        })
    }

    private fun fetchAlbumArt() {
        val req = Request.Builder().url(mediaArtUrl).build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) {
                mainHandler.post { clearAlbumArt() }
            }
            override fun onResponse(call: okhttp3.Call, response: Response) {
                response.use { res ->
                    if (res.code == 200) {
                        val bytes = res.body?.bytes()
                        if (bytes != null && bytes.isNotEmpty()) {
                            val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                            if (bmp != null) {
                                val blurred = fastBlur(bmp, 12)
                                val vibrant = extractVibrantColor(bmp)
                                mainHandler.post {
                                    ivMediaArt.setImageBitmap(bmp)
                                    ivMediaArt.visibility = View.VISIBLE
                                    ivMediaBackdrop.setImageBitmap(blurred)
                                    applyMediaAccent(vibrant)
                                }
                                return
                            }
                        }
                    }
                    mainHandler.post { clearAlbumArt() }
                }
            }
        })
    }

    private fun clearAlbumArt() {
        ivMediaArt.visibility = View.GONE
        ivMediaBackdrop.setImageBitmap(null)
        applyMediaAccent((themes[currentTheme] ?: themes["midnight"]!!).accent)
    }

    private fun applyMediaAccent(color: Int) {
        mediaAccentColor = color
        tvMediaStatus.setTextColor(color)
        audioVisualizer.setAccentColor(color)
        mediaProgress.progressTintList = ColorStateList.valueOf(color)
        mediaProgress.progressBackgroundTintList = ColorStateList.valueOf(Color.parseColor("#33FFFFFF"))
        btnPlayPause.background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
        }
    }

    private fun extractVibrantColor(bmp: Bitmap): Int {
        val small = Bitmap.createScaledBitmap(bmp, 16, 16, true)
        var bestColor = Color.parseColor("#58A6FF")
        var bestScore = -1f
        val hsv = FloatArray(3)
        for (y in 0 until 16) {
            for (x in 0 until 16) {
                val c = small.getPixel(x, y)
                Color.colorToHSV(c, hsv)
                val sat = hsv[1]
                val v = hsv[2]
                if (v > 0.28f && sat > 0.25f) {
                    val score = sat * 1.4f + v * 0.6f
                    if (score > bestScore) {
                        bestScore = score
                        hsv[2] = v.coerceIn(0.68f, 0.96f)
                        hsv[1] = sat.coerceIn(0.50f, 0.92f)
                        bestColor = Color.HSVToColor(hsv)
                    }
                }
            }
        }
        small.recycle()
        return bestColor
    }

    private fun fastBlur(src: Bitmap, radius: Int): Bitmap {
        val w = 48
        val h = 48
        val bmp = Bitmap.createScaledBitmap(src, w, h, true)
        val pix = IntArray(w * h)
        bmp.getPixels(pix, 0, w, 0, 0, w, h)
        val tmp = IntArray(w * h)
        val r = radius.coerceIn(2, 16)
        repeat(2) {
            for (y in 0 until h) {
                for (x in 0 until w) {
                    var rs = 0; var gs = 0; var bs = 0; var cnt = 0
                    for (dx in -r..r) {
                        val nx = (x + dx).coerceIn(0, w - 1)
                        val c = pix[y * w + nx]
                        rs += Color.red(c); gs += Color.green(c); bs += Color.blue(c); cnt++
                    }
                    tmp[y * w + x] = Color.rgb(rs / cnt, gs / cnt, bs / cnt)
                }
            }
            for (y in 0 until h) {
                for (x in 0 until w) {
                    var rs = 0; var gs = 0; var bs = 0; var cnt = 0
                    for (dy in -r..r) {
                        val ny = (y + dy).coerceIn(0, h - 1)
                        val c = tmp[ny * w + x]
                        rs += Color.red(c); gs += Color.green(c); bs += Color.blue(c); cnt++
                    }
                    pix[y * w + x] = Color.rgb(rs / cnt, gs / cnt, bs / cnt)
                }
            }
        }
        val out = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        out.setPixels(pix, 0, w, 0, 0, w, h)
        bmp.recycle()
        return out
    }

    private fun formatTime(sec: Double?): String {
        if (sec == null || sec.isNaN() || sec < 0) return "0:00"
        val total = sec.roundToInt()
        return "%d:%02d".format(total / 60, total % 60)
    }

    private fun applyTheme() {
        val th = themes[currentTheme] ?: themes["midnight"]!!
        rootContainer.setBackgroundColor(th.bg)
        macroOverlay.setBackgroundColor(Color.argb(245, Color.red(th.bg), Color.green(th.bg), Color.blue(th.bg)))
        cards.values.forEach { holder ->
            holder.cardRoot.background = makeCardDrawable(th.card, th.border)
            holder.tvValue.setTextSize(TypedValue.COMPLEX_UNIT_SP, 34f * currentFontScale)
        }
        rebuildMacroGrid()
    }

    private fun makeCardDrawable(fill: Int, stroke: Int): GradientDrawable =
        GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            setColor(fill)
            setStroke(2, stroke)
            cornerRadius = 28f
        }

    private fun rebuildMacroGrid() {
        macroGrid.removeAllViews()
        val th = themes[currentTheme] ?: themes["midnight"]!!
        val isLandscape = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        val count = macros.size.coerceAtLeast(1)
        val cols = if (isLandscape) 3 else 2
        val rows = ceil(count.toDouble() / cols).toInt().coerceAtLeast(1)
        macroGrid.columnCount = cols
        macroGrid.rowCount = rows

        macros.forEachIndexed { idx, m ->
            val btn = TextView(this).apply {
                text = "${m.label}\n${m.hotkey}"
                setTextColor(Color.parseColor("#E6EDF3"))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
                gravity = Gravity.CENTER
                background = makeCardDrawable(th.card, th.border)
                setPadding(16, 16, 16, 16)
                setOnClickListener { v ->
                    v.animate().scaleX(0.94f).scaleY(0.94f).setDuration(80).withEndAction {
                        v.animate().scaleX(1f).scaleY(1f).setDuration(90).start()
                    }.start()
                    sendPost(macroUrl, JSONObject().put("hotkey", m.hotkey))
                }
            }
            val params = GridLayout.LayoutParams(
                GridLayout.spec(idx / cols, 1f),
                GridLayout.spec(idx % cols, 1f)
            ).apply {
                width = 0
                height = 0
                setMargins(10, 10, 10, 10)
            }
            btn.layoutParams = params
            macroGrid.addView(btn)
        }
    }

    override fun onResume() {
        super.onResume()
        applyImmersiveMode()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        rebuildGrid()
        rebuildMacroGrid()
        lastPayload?.let { updateUi(it) }
    }

    override fun onDestroy() {
        isDestroyedFlag = true
        webSocket?.close(1000, "Activity destroyed")
        client.dispatcher.executorService.shutdown()
        super.onDestroy()
    }

    @Suppress("DEPRECATION")
    private fun applyImmersiveMode() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            WindowCompat.setDecorFitsSystemWindows(window, false)
            WindowInsetsControllerCompat(window, window.decorView).let { ctrl ->
                ctrl.hide(WindowInsetsCompat.Type.systemBars())
                ctrl.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = (
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                    or View.SYSTEM_UI_FLAG_FULLSCREEN
                    or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                    or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                    or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                )
        }
    }

    private fun cycleOrientation() {
        orientationMode = (orientationMode + 1) % 3
        requestedOrientation = when (orientationMode) {
            1 -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            2 -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
            else -> ActivityInfo.SCREEN_ORIENTATION_SENSOR
        }
    }

    private fun toggleDim() {
        isDimmed = !isDimmed
        val lp = window.attributes
        lp.screenBrightness = if (isDimmed) 0.08f else WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        window.attributes = lp
    }

    private fun loadSavedMetrics() {
        val prefs = getSharedPreferences("pc_monitor_prefs", MODE_PRIVATE)
        val saved = prefs.getString("enabled_metrics", null) ?: return
        val list = saved.split(",").filter { it in allMetricIds }
        if (list.isNotEmpty()) enabledMetrics = list.toMutableList()
    }

    private fun saveMetricsLocally() {
        getSharedPreferences("pc_monitor_prefs", MODE_PRIVATE)
            .edit()
            .putString("enabled_metrics", enabledMetrics.joinToString(","))
            .apply()
    }

    private fun rebuildGrid() {
        metricsGrid.removeAllViews()
        cards.clear()

        val th = themes[currentTheme] ?: themes["midnight"]!!
        val isLandscape = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        val count = enabledMetrics.size.coerceAtLeast(1)
        val cols = when {
            count <= 2 -> count
            isLandscape -> if (count <= 6) 3 else 4
            else -> 2
        }
        val rows = ceil(count.toDouble() / cols).toInt().coerceAtLeast(1)

        metricsGrid.columnCount = cols
        metricsGrid.rowCount = rows

        val inflater = LayoutInflater.from(this)
        enabledMetrics.forEachIndexed { index, id ->
            val card = inflater.inflate(R.layout.item_metric_card, metricsGrid, false)
            val params = GridLayout.LayoutParams(
                GridLayout.spec(index / cols, 1f),
                GridLayout.spec(index % cols, 1f)
            ).apply {
                width = 0
                height = 0
                setMargins(8, 8, 8, 8)
            }
            card.layoutParams = params

            val holder = CardHolder(
                cardRoot = card.findViewById(R.id.cardRoot),
                tvLabel = card.findViewById(R.id.tvLabel),
                tvValue = card.findViewById(R.id.tvValue),
                tvUnit = card.findViewById(R.id.tvUnit),
                tvSub = card.findViewById(R.id.tvSub),
                sparkline = card.findViewById(R.id.sparkline),
                progress = card.findViewById(R.id.progressBar)
            )
            holder.cardRoot.background = makeCardDrawable(th.card, th.border)
            holder.tvValue.setTextSize(TypedValue.COMPLEX_UNIT_SP, 34f * currentFontScale)
            holder.tvLabel.text = metricLabels[id] ?: id
            cards[id] = holder
            metricsGrid.addView(card)
        }
    }

    private fun connectWebSocket() {
        if (isDestroyedFlag) return
        val request = Request.Builder().url(wsUrl).build()
        webSocket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                mainHandler.post {
                    tvStatus.text = "● USB подключён"
                    tvStatus.setTextColor(Color.parseColor("#3FB950"))
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                try {
                    val json = JSONObject(text)
                    lastPayload = json
                    mainHandler.post { updateUi(json) }
                } catch (_: Exception) { }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = scheduleReconnect()
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = scheduleReconnect()
        })
    }

    private fun scheduleReconnect() {
        if (isDestroyedFlag) return
        mainHandler.post {
            tvStatus.text = "○ Ожидание ПК по USB..."
            tvStatus.setTextColor(Color.parseColor("#D29922"))
        }
        mainHandler.postDelayed({ connectWebSocket() }, 2000)
    }

    private fun updateUi(d: JSONObject) {
        val remoteArr = d.optJSONArray("enabledMetrics")
        if (remoteArr != null && remoteArr.length() > 0) {
            val remoteList = mutableListOf<String>()
            for (i in 0 until remoteArr.length()) {
                val id = remoteArr.optString(i)
                if (id in allMetricIds) remoteList.add(id)
            }
            if (remoteList.isNotEmpty() && remoteList != enabledMetrics) {
                enabledMetrics = remoteList
                saveMetricsLocally()
                rebuildGrid()
            }
        }

        val remoteTheme = d.optString("theme", currentTheme)
        val remoteScale = d.optDouble("fontScale", currentFontScale.toDouble()).toFloat().coerceIn(0.75f, 1.45f)
        if (remoteTheme != currentTheme || abs(remoteScale - currentFontScale) > 0.01f) {
            currentTheme = if (remoteTheme in themes) remoteTheme else "midnight"
            currentFontScale = remoteScale
            applyTheme()
        }

        val remoteMacros = d.optJSONArray("macros")
        if (remoteMacros != null && remoteMacros.length() > 0) {
            val parsed = mutableListOf<MacroItem>()
            for (i in 0 until remoteMacros.length()) {
                val obj = remoteMacros.optJSONObject(i) ?: continue
                parsed.add(MacroItem(obj.optString("label", "Кнопка"), obj.optString("hotkey", "")))
            }
            if (parsed.isNotEmpty() && parsed != macros) {
                macros = parsed
                rebuildMacroGrid()
            }
        }

        val game = d.optString("activeGame", "")
        tvGameName.text = if (game.isNotEmpty() && game != "null") "Игра: $game" else ""

        // Next-Gen Media Player updates
        val mTitle = if (d.isNull("mediaTitle")) "" else d.optString("mediaTitle", "")
        val mArtist = if (d.isNull("mediaArtist")) "" else d.optString("mediaArtist", "")
        val mPlaying = d.optBoolean("mediaPlaying", false)
        val artVer = d.optInt("mediaArtVersion", 0)
        val posSec = optNullableDouble(d, "mediaPosSec")
        val durSec = optNullableDouble(d, "mediaDurSec")

        if (artVer != lastArtVersion) {
            lastArtVersion = artVer
            if (mTitle.isNotEmpty()) fetchAlbumArt() else clearAlbumArt()
        }

        if (lastMediaPlaying != mPlaying) {
            lastMediaPlaying = mPlaying
            audioVisualizer.setPlaying(mPlaying && mTitle.isNotEmpty())
            val scale = if (mPlaying) 1.0f else 0.88f
            ivMediaArt.animate().scaleX(scale).scaleY(scale).setDuration(260).start()
        }

        if (mTitle.isNotEmpty()) {
            val icon = if (mPlaying) "▶" else "⏸"
            tvMiniMedia.text = "⌃ $icon $mTitle" + if (mArtist.isNotEmpty()) " — $mArtist" else ""
            tvMediaStatus.text = if (mPlaying) "NOW PLAYING" else "PAUSED"
            tvMediaTitle.text = mTitle
            tvMediaArtist.text = mArtist
            btnPlayPause.setImageResource(if (mPlaying) R.drawable.ic_pause else R.drawable.ic_play)
            tvMediaPos.text = formatTime(posSec)
            tvMediaDur.text = formatTime(durSec)
            if (posSec != null && durSec != null && durSec > 0) {
                mediaProgress.progress = ((posSec / durSec) * 1000.0).roundToInt().coerceIn(0, 1000)
            } else {
                mediaProgress.progress = 0
            }
        } else {
            tvMiniMedia.text = "⌃ Свайп вверх — плеер · ⇠ Свайп влево — кнопки"
            tvMediaStatus.text = "WINDOWS MEDIA"
            tvMediaTitle.text = "Ничего не играет"
            tvMediaArtist.text = ""
            btnPlayPause.setImageResource(R.drawable.ic_play)
            tvMediaPos.text = "0:00"
            tvMediaDur.text = "0:00"
            mediaProgress.progress = 0
            audioVisualizer.setPlaying(false)
            clearAlbumArt()
        }

        val accentColor = (themes[currentTheme] ?: themes["midnight"]!!).accent

        cards["fps"]?.let { c ->
            val fps = if (d.isNull("fps")) null else d.optInt("fps")
            val low1 = if (d.isNull("fps1Low")) null else d.optInt("fps1Low")
            if (fps != null) {
                c.tvValue.text = fps.toString(); c.tvUnit.text = "FPS"
                c.tvSub.text = if (low1 != null) "1% low: $low1 FPS" else ""
                val col = when {
                    fps >= 60 -> Color.parseColor("#3FB950")
                    fps >= 30 -> Color.parseColor("#D29922")
                    else -> Color.parseColor("#F85149")
                }
                setCardData(c, col, (fps * 100 / 165).coerceIn(0, 100), fps / 165f)
            } else {
                c.tvValue.text = "--"; c.tvUnit.text = "FPS"
                c.tvSub.text = if (d.optBoolean("presentMonActive", false)) "Нет активной игры" else "PresentMon не запущен"
                setCardData(c, Color.parseColor("#8B949E"), 0, null)
            }
        }

        updatePercentCard("cpu_load", d, "cpuLoad", d.optString("cpuName", ""), accentColor)
        updateTempCard("cpu_temp", d, "cpuTemp", "Tctl / Package")
        updatePercentCard("gpu_load", d, "gpuLoad", d.optString("gpuName", ""), accentColor)
        updateTempCard("gpu_temp", d, "gpuTemp", "GPU Core")

        cards["ram_load"]?.let { c ->
            val pct = optNullableDouble(d, "ramLoad")
            val used = optNullableDouble(d, "ramUsedGb")
            val total = optNullableDouble(d, "ramTotalGb")
            if (pct != null) {
                c.tvValue.text = pct.roundToInt().toString(); c.tvUnit.text = "%"
                c.tvSub.text = if (used != null && total != null) "$used / $total ГБ" else ""
                setCardData(c, loadColor(pct, accentColor), pct.roundToInt().coerceIn(0, 100), (pct / 100.0).toFloat())
            } else {
                c.tvValue.text = "--"; c.tvUnit.text = "%"; c.tvSub.text = ""
                setCardData(c, Color.parseColor("#8B949E"), 0, null)
            }
        }

        cards["gpu_vram"]?.let { c ->
            val used = optNullableDouble(d, "gpuVramUsedGb")
            val total = optNullableDouble(d, "gpuVramTotalGb")
            if (used != null) {
                c.tvValue.text = String.format("%.1f", used); c.tvUnit.text = "ГБ"
                val pct = if (total != null && total > 0) ((used / total) * 100).roundToInt() else 0
                c.tvSub.text = if (total != null) "из ${String.format("%.1f", total)} ГБ ($pct%)" else ""
                setCardData(c, loadColor(pct.toDouble(), accentColor), pct.coerceIn(0, 100), pct / 100f)
            } else {
                c.tvValue.text = "--"; c.tvUnit.text = "ГБ"; c.tvSub.text = ""
                setCardData(c, Color.parseColor("#8B949E"), 0, null)
            }
        }

        updateSimpleCard("cpu_power", d, "cpuPower", "Вт", 120.0, accentColor)
        updateSimpleCard("gpu_power", d, "gpuPower", "Вт", 200.0, accentColor)
        updateSimpleCard("cpu_clock", d, "cpuClock", "МГц", 5000.0, accentColor)
        updateSimpleCard("gpu_clock", d, "gpuClock", "МГц", 3000.0, accentColor)
    }

    private fun updatePercentCard(id: String, d: JSONObject, key: String, sub: String, accent: Int) {
        val c = cards[id] ?: return
        val v = optNullableDouble(d, key)
        if (v != null) {
            c.tvValue.text = v.roundToInt().toString(); c.tvUnit.text = "%"; c.tvSub.text = sub
            setCardData(c, loadColor(v, accent), v.roundToInt().coerceIn(0, 100), (v / 100.0).toFloat())
        } else {
            c.tvValue.text = "--"; c.tvUnit.text = "%"; c.tvSub.text = ""
            setCardData(c, Color.parseColor("#8B949E"), 0, null)
        }
    }

    private fun updateTempCard(id: String, d: JSONObject, key: String, sub: String) {
        val c = cards[id] ?: return
        val v = optNullableDouble(d, key)
        if (v != null) {
            c.tvValue.text = v.roundToInt().toString(); c.tvUnit.text = "°C"; c.tvSub.text = sub
            val col = when {
                v >= 85.0 -> Color.parseColor("#F85149")
                v >= 72.0 -> Color.parseColor("#D29922")
                else -> Color.parseColor("#3FB950")
            }
            setCardData(c, col, v.roundToInt().coerceIn(0, 100), (v / 100.0).toFloat())
        } else {
            c.tvValue.text = "--"; c.tvUnit.text = "°C"; c.tvSub.text = ""
            setCardData(c, Color.parseColor("#8B949E"), 0, null)
        }
    }

    private fun updateSimpleCard(id: String, d: JSONObject, key: String, unit: String, maxVal: Double, color: Int) {
        val c = cards[id] ?: return
        val v = optNullableDouble(d, key)
        if (v != null) {
            c.tvValue.text = v.roundToInt().toString(); c.tvUnit.text = unit; c.tvSub.text = ""
            val norm = (v / maxVal).toFloat()
            setCardData(c, color, (norm * 100).roundToInt().coerceIn(0, 100), norm)
        } else {
            c.tvValue.text = "--"; c.tvUnit.text = unit; c.tvSub.text = ""
            setCardData(c, Color.parseColor("#8B949E"), 0, null)
        }
    }

    private fun loadColor(pct: Double, accent: Int): Int = when {
        pct >= 90.0 -> Color.parseColor("#F85149")
        pct >= 75.0 -> Color.parseColor("#D29922")
        else -> accent
    }

    private fun setCardData(c: CardHolder, color: Int, progress: Int, sparkNorm: Float?) {
        c.tvValue.setTextColor(color)
        c.progress.progress = progress
        c.progress.progressTintList = ColorStateList.valueOf(color)
        if (sparkNorm != null) c.sparkline.addPoint(sparkNorm, color)
    }

    private fun optNullableDouble(d: JSONObject, key: String): Double? {
        if (!d.has(key) || d.isNull(key)) return null
        val v = d.optDouble(key, Double.NaN)
        return if (v.isNaN()) null else v
    }

    private fun openSettingsDialog() {
        val labels = allMetricIds.map { metricLabels[it] ?: it }.toTypedArray()
        val checked = BooleanArray(allMetricIds.size) { idx -> allMetricIds[idx] in enabledMetrics }

        AlertDialog.Builder(this)
            .setTitle("Какие показатели выводить")
            .setMultiChoiceItems(labels, checked) { _, which, isChecked -> checked[which] = isChecked }
            .setPositiveButton("Сохранить") { _, _ ->
                val selected = allMetricIds.filterIndexed { idx, _ -> checked[idx] }
                if (selected.isNotEmpty()) {
                    enabledMetrics = selected.toMutableList()
                    saveMetricsLocally()
                    rebuildGrid()
                    lastPayload?.let { updateUi(it) }
                    sendPost(configUrl, JSONObject().put("enabledMetrics", JSONArray(selected)))
                }
            }
            .setNegativeButton("Отмена", null)
            .show()
    }
}
