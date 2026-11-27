package com.pcmonitor.usb

import android.app.AlertDialog
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ActivityInfo
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Outline
import android.graphics.drawable.GradientDrawable
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.TypedValue
import android.view.GestureDetector
import android.view.HapticFeedbackConstants
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewOutlineProvider
import android.view.WindowManager
import android.widget.GridLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import android.widget.Toast
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
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.TimeUnit
import kotlin.math.abs
import kotlin.math.ceil
import kotlin.math.roundToInt

class MainActivity : AppCompatActivity() {

    private val wsUrl = "ws://127.0.0.1:8765/ws"
    private val mediaUrl = "http://127.0.0.1:8765/api/media"
    private val mediaArtUrl = "http://127.0.0.1:8765/api/media/art"
    private val macroUrl = "http://127.0.0.1:8765/api/macro"
    private val obsUrl = "http://127.0.0.1:8765/api/obs"
    private val wallpaperUrl = "http://127.0.0.1:8765/api/wallpaper"
    private val updateApkUrl = "http://127.0.0.1:8765/api/update-apk"
    private val presetUrl = "http://127.0.0.1:8765/api/preset"
    private val ocUrl = "http://127.0.0.1:8765/api/oc"

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
    private var isStandbyOpen = false
    private var manualStandbyOverride = false
    private var lastArtVersion = -1
    private var lastWallpaperVersion = -1
    private var hasCustomWallpaper = false
    private var currentAlbumBmp: Bitmap? = null
    private var lastMediaPlaying: Boolean? = null
    private var lastGameSeenMs = System.currentTimeMillis()
    private var alertPulseToggle = false
    private var pixelShiftStep = 0

    private var currentTheme = "midnight"
    private var currentFontScale = 1.0f
    private var mediaAccentColor = Color.parseColor("#38BDF8")

    private data class ThemeColors(val bgTop: Int, val bgBot: Int, val card: Int, val border: Int, val accent: Int)
    private val themes = mapOf(
        "midnight" to ThemeColors(Color.parseColor("#13283F"), Color.parseColor("#070B12"), Color.parseColor("#121A26"), Color.parseColor("#2A3A4F"), Color.parseColor("#38BDF8")),
        "amoled"   to ThemeColors(Color.parseColor("#000000"), Color.parseColor("#000000"), Color.parseColor("#0A0A0A"), Color.parseColor("#1F242C"), Color.parseColor("#00E676")),
        "spide"    to ThemeColors(Color.parseColor("#291A14"), Color.parseColor("#0E0A08"), Color.parseColor("#1D1512"), Color.parseColor("#423027"), Color.parseColor("#FF8A50")),
        "cyber"    to ThemeColors(Color.parseColor("#22103B"), Color.parseColor("#08050F"), Color.parseColor("#160E26"), Color.parseColor("#3D2566"), Color.parseColor("#D946EF"))
    )

    private data class BentoBlock(val id: String, val type: String, val x: Int, val y: Int, val w: Int, val h: Int, val colorHex: String)
    private var layoutCols = 4
    private var layoutRows = 3
    private var layoutBlocks: List<BentoBlock> = listOf(
        BentoBlock("b1", "clock_weather", 0, 0, 2, 2, "#38BDF8"),
        BentoBlock("b2", "media_player",  2, 0, 2, 2, "#38BDF8"),
        BentoBlock("b3", "cpu",           0, 2, 1, 1, "#22D3EE"),
        BentoBlock("b4", "gpu",           1, 2, 1, 1, "#84CC16"),
        BentoBlock("b5", "ram",           2, 2, 1, 1, "#A78BFA"),
        BentoBlock("b6", "fps",           3, 2, 1, 1, "#F59E0B")
    )
    private var lastLayoutSignature = ""

    private data class MacroItem(val label: String, val hotkey: String)
    private var macros: List<MacroItem> = listOf(
        MacroItem("🎙 Мут мик.", "Ctrl+Shift+M"),
        MacroItem("🔴 Запись", "Ctrl+Shift+R"),
        MacroItem("🎬 Сцена 1", "Ctrl+Shift+1"),
        MacroItem("🎬 Сцена 2", "Ctrl+Shift+2"),
        MacroItem("📸 Скриншот", "Win+PrintScreen"),
        MacroItem("🔇 Звук ПК", "vol_mute")
    )
    private var lastObsScenesKey = ""

    private val blockViews = mutableMapOf<String, View>()
    private var lastPayload: JSONObject? = null

    private lateinit var gestureDetector: GestureDetector
    private lateinit var rootContainer: View
    private lateinit var rootLayout: View
    private lateinit var ivCustomBg: ImageView
    private lateinit var customBgScrim: View
    private lateinit var tvStatus: TextView
    private lateinit var tvGameName: TextView
    private lateinit var btnUpdateApp: TextView
    private lateinit var btnPreset: TextView
    private lateinit var metricsGrid: GridLayout
    private lateinit var tvMiniMedia: TextView
    private lateinit var standbyOverlay: View
    private lateinit var tvClockTime: TextView
    private lateinit var tvClockDate: TextView
    private lateinit var tvClockMedia: TextView
    private lateinit var mediaOverlay: View
    private lateinit var mediaBody: LinearLayout
    private lateinit var mediaRightCol: LinearLayout
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
    private lateinit var obsBar: View
    private lateinit var btnObsStream: TextView
    private lateinit var btnObsRecord: TextView
    private lateinit var obsScenesRow: LinearLayout
    private lateinit var macroGrid: GridLayout

    private val timeFmt = SimpleDateFormat("HH:mm", Locale.getDefault())
    private val secFmt = SimpleDateFormat("ss", Locale.getDefault())
    private val dateFmt = SimpleDateFormat("EEEE, d MMMM", Locale("ru"))

    private val clockAndShiftRunnable = object : Runnable {
        override fun run() {
            if (isDestroyedFlag) return
            val now = Date()
            val hhmm = timeFmt.format(now)
            val ss = secFmt.format(now)
            val dateStr = dateFmt.format(now)

            tvClockTime.text = hhmm
            tvClockDate.text = dateStr.replaceFirstChar { it.uppercase() }

            // Update any inline clock_weather Bento blocks every second
            layoutBlocks.filter { it.type == "clock_weather" }.forEach { b ->
                val v = blockViews[b.id] ?: return@forEach
                v.findViewById<TextView>(R.id.tvBlockTime)?.text = hhmm
                v.findViewById<TextView>(R.id.tvBlockSec)?.text = ss
                v.findViewById<TextView>(R.id.tvBlockDate)?.text = dateStr
            }

            pixelShiftStep = (pixelShiftStep + 1) % 120
            if (pixelShiftStep == 0) {
                val offsets = listOf(-3f to -2f, 3f to -2f, 3f to 2f, -3f to 2f, 0f to 0f)
                val (dx, dy) = offsets[(System.currentTimeMillis() / 120_000L % offsets.size).toInt()]
                rootLayout.animate().translationX(dx).translationY(dy).setDuration(600).start()
                standbyOverlay.animate().translationX(dx).translationY(dy).setDuration(600).start()
            }
            mainHandler.postDelayed(this, 1000L)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)

        rootContainer = findViewById(R.id.rootContainer)
        rootLayout = findViewById(R.id.rootLayout)
        ivCustomBg = findViewById(R.id.ivCustomBg)
        customBgScrim = findViewById(R.id.customBgScrim)
        tvStatus = findViewById(R.id.tvStatus)
        tvGameName = findViewById(R.id.tvGameName)
        btnUpdateApp = findViewById(R.id.btnUpdateApp)
        btnPreset = findViewById(R.id.btnPreset)
        metricsGrid = findViewById(R.id.metricsGrid)
        tvMiniMedia = findViewById(R.id.tvMiniMedia)
        standbyOverlay = findViewById(R.id.standbyOverlay)
        tvClockTime = findViewById(R.id.tvClockTime)
        tvClockDate = findViewById(R.id.tvClockDate)
        tvClockMedia = findViewById(R.id.tvClockMedia)
        mediaOverlay = findViewById(R.id.mediaOverlay)
        mediaBody = findViewById(R.id.mediaBody)
        mediaRightCol = findViewById(R.id.mediaRightCol)
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
        obsBar = findViewById(R.id.obsBar)
        btnObsStream = findViewById(R.id.btnObsStream)
        btnObsRecord = findViewById(R.id.btnObsRecord)
        obsScenesRow = findViewById(R.id.obsScenesRow)
        macroGrid = findViewById(R.id.macroGrid)

        ivMediaArt.clipToOutline = true
        ivMediaArt.outlineProvider = roundOutline(36f)

        findViewById<View>(R.id.btnSettings).setOnClickListener {
            Toast.makeText(this, "Настрой блоки в студии на ПК: http://127.0.0.1:8765/", Toast.LENGTH_LONG).show()
        }
        findViewById<View>(R.id.btnRotate).setOnClickListener { cycleOrientation() }
        findViewById<View>(R.id.btnDim).setOnClickListener { toggleDim() }
        findViewById<View>(R.id.btnClockMode).setOnClickListener {
            manualStandbyOverride = true
            setStandbyVisible(!isStandbyOpen)
        }
        standbyOverlay.setOnClickListener { manualStandbyOverride = true; setStandbyVisible(false) }
        findViewById<View>(R.id.btnMacros).setOnClickListener { showMacroOverlay(true) }
        findViewById<View>(R.id.btnCloseMacros).setOnClickListener { showMacroOverlay(false) }
        tvMiniMedia.setOnClickListener { showMediaOverlay(true) }
        findViewById<View>(R.id.btnCloseMedia).setOnClickListener { showMediaOverlay(false) }
        btnUpdateApp.setOnClickListener { triggerUsbSelfUpdate() }
        btnPreset.setOnClickListener { v ->
            v.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
            sendPost(presetUrl, JSONObject().put("preset", "next"))
        }
        btnPreset.setOnLongClickListener {
            val names = arrayOf("✨ Главный (4×3)", "🎮 Игровой (3×2)", "📺 Стример (5×3)", "🔥 Разгон (4×3)")
            val ids = arrayOf("tiktok", "gamer", "streamer", "oc")
            AlertDialog.Builder(this)
                .setTitle("Выбери режим экрана")
                .setItems(names) { _, which ->
                    sendPost(presetUrl, JSONObject().put("preset", ids[which]))
                }
                .show()
            true
        }

        btnObsStream.setOnClickListener {
            it.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
            sendPost(obsUrl, JSONObject().put("action", "toggle_stream"))
        }
        btnObsRecord.setOnClickListener {
            it.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
            sendPost(obsUrl, JSONObject().put("action", "toggle_record"))
        }

        setupBouncyMediaBtn(findViewById(R.id.btnPrev), "prev")
        setupBouncyMediaBtn(btnPlayPause, "play_pause")
        setupBouncyMediaBtn(findViewById(R.id.btnNext), "next")
        setupBouncyMediaBtn(findViewById(R.id.btnVolDown), "vol_down")
        setupBouncyMediaBtn(findViewById(R.id.btnVolMute), "vol_mute")
        setupBouncyMediaBtn(findViewById(R.id.btnVolUp), "vol_up")

        gestureDetector = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onFling(e1: MotionEvent?, e2: MotionEvent, velocityX: Float, velocityY: Float): Boolean {
                if (e1 == null) return false
                val dx = e2.x - e1.x; val dy = e2.y - e1.y
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

        rebuildBentoGrid()
        rebuildMacroGrid()
        applyMediaOrientationLayout()
        applyTheme()
        applyMediaAccent(mediaAccentColor)
        applyImmersiveMode()
        mainHandler.post(clockAndShiftRunnable)
        connectWebSocket()
    }

    private fun roundOutline(radius: Float) = object : ViewOutlineProvider() {
        override fun getOutline(view: View, outline: Outline) {
            outline.setRoundRect(0, 0, view.width, view.height, radius)
        }
    }

    private fun parseColorSafe(hex: String, fallback: Int): Int =
        try { Color.parseColor(hex) } catch (_: Exception) { fallback }

    private fun getBatteryText(): String {
        val intent = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return ""
        val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
        val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val charging = status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL
        if (level < 0) return ""
        val pct = (level * 100f / scale.coerceAtLeast(1)).roundToInt()
        return " · 🔋 $pct%" + if (charging) " ⚡" else ""
    }

    private fun makeGlassBlockBg(accent: Int, criticalPulse: Boolean = false): GradientDrawable {
        val th = themes[currentTheme] ?: themes["midnight"]!!
        if (criticalPulse) {
            return GradientDrawable().apply {
                setColor(Color.argb(215, 78, 16, 20))
                setStroke(4, Color.parseColor("#F85149"))
                cornerRadius = 34f
            }
        }
        val alpha = if (hasCustomWallpaper) 180 else 210
        val topCol = Color.argb(
            alpha,
            (Color.red(th.card) * 4 + Color.red(accent)) / 5,
            (Color.green(th.card) * 4 + Color.green(accent)) / 5,
            (Color.blue(th.card) * 4 + Color.blue(accent)) / 5
        )
        val botCol = Color.argb(alpha, Color.red(th.card), Color.green(th.card), Color.blue(th.card))
        val stroke = Color.argb(85, Color.red(accent), Color.green(accent), Color.blue(accent))
        return GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(topCol, botCol)).apply {
            cornerRadius = 34f
            setStroke(2, stroke)
        }
    }

    private fun rebuildBentoGrid() {
        metricsGrid.removeAllViews()
        blockViews.clear()

        metricsGrid.columnCount = layoutCols.coerceIn(2, 6)
        metricsGrid.rowCount = layoutRows.coerceIn(2, 5)

        val inflater = LayoutInflater.from(this)
        val th = themes[currentTheme] ?: themes["midnight"]!!

        layoutBlocks.forEach { b ->
            val accent = parseColorSafe(b.colorHex, th.accent)
            val view = when (b.type) {
                "clock_weather" -> inflater.inflate(R.layout.block_clock_weather, metricsGrid, false)
                "oc_controls" -> {
                    val v = inflater.inflate(R.layout.block_oc_controls, metricsGrid, false)
                    fun wireOc(id: Int, prof: String) {
                        v.findViewById<View>(id).setOnClickListener { btn ->
                            btn.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                            btn.animate().scaleX(0.92f).scaleY(0.92f).setDuration(70).withEndAction {
                                btn.animate().scaleX(1f).scaleY(1f).setDuration(100).start()
                            }.start()
                            sendPost(ocUrl, JSONObject().put("profile", prof))
                        }
                    }
                    wireOc(R.id.btnOcStock, "stock")
                    wireOc(R.id.btnOcMild, "mild")
                    wireOc(R.id.btnOcStandard, "standard")
                    wireOc(R.id.btnOcExtreme, "extreme")
                    v
                }
                "media_player" -> {
                    val v = inflater.inflate(R.layout.block_media_player, metricsGrid, false)
                    val art = v.findViewById<ImageView>(R.id.ivBlkArt)
                    art.clipToOutline = true
                    art.outlineProvider = roundOutline(24f)
                    currentAlbumBmp?.let { art.setImageBitmap(it) }
                    setupBouncyMediaBtn(v.findViewById(R.id.btnBlkPrev), "prev")
                    setupBouncyMediaBtn(v.findViewById(R.id.btnBlkPlay), "play_pause")
                    setupBouncyMediaBtn(v.findViewById(R.id.btnBlkNext), "next")
                    setupBouncyMediaBtn(v.findViewById(R.id.btnBlkVolDown), "vol_down")
                    setupBouncyMediaBtn(v.findViewById(R.id.btnBlkVolUp), "vol_up")
                    v
                }
                else -> {
                    val v = inflater.inflate(R.layout.block_metric, metricsGrid, false)
                    v.findViewById<TextView>(R.id.tvBlkLabel).setTextColor(accent)
                    v.findViewById<TextView>(R.id.tvBlkVal).setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f * currentFontScale)
                    v
                }
            }

            if (b.type != "clock_weather") {
                view.background = makeGlassBlockBg(accent)
            } else {
                view.background = null
            }

            val cx = b.x.coerceIn(0, layoutCols - 1)
            val cy = b.y.coerceIn(0, layoutRows - 1)
            val cw = b.w.coerceIn(1, layoutCols - cx)
            val ch = b.h.coerceIn(1, layoutRows - cy)

            val params = GridLayout.LayoutParams(
                GridLayout.spec(cy, ch, ch.toFloat()),
                GridLayout.spec(cx, cw, cw.toFloat())
            ).apply {
                width = 0
                height = 0
                setMargins(8, 8, 8, 8)
            }
            view.layoutParams = params
            blockViews[b.id] = view
            metricsGrid.addView(view)
        }
    }

    private fun applyTheme() {
        val th = themes[currentTheme] ?: themes["midnight"]!!
        rootContainer.background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(th.bgTop, th.bgBot))
        macroOverlay.setBackgroundColor(Color.argb(245, Color.red(th.bgBot), Color.green(th.bgBot), Color.blue(th.bgBot)))
        layoutBlocks.forEach { b ->
            val v = blockViews[b.id] ?: return@forEach
            val accent = parseColorSafe(b.colorHex, th.accent)
            if (b.type != "clock_weather") v.background = makeGlassBlockBg(accent)
            if (b.type != "clock_weather" && b.type != "media_player" && b.type != "oc_controls") {
                v.findViewById<TextView>(R.id.tvBlkVal)?.setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f * currentFontScale)
            }
        }
        rebuildMacroGrid()
    }

    private fun setStandbyVisible(show: Boolean) {
        if (show == isStandbyOpen) return
        isStandbyOpen = show
        if (show) {
            standbyOverlay.alpha = 0f; standbyOverlay.visibility = View.VISIBLE
            standbyOverlay.animate().alpha(1f).setDuration(260).start()
        } else {
            standbyOverlay.animate().alpha(0f).setDuration(200).withEndAction { standbyOverlay.visibility = View.GONE }.start()
        }
    }

    private fun fetchCustomWallpaper() {
        val req = Request.Builder().url(wallpaperUrl).build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) { mainHandler.post { clearCustomWallpaper() } }
            override fun onResponse(call: okhttp3.Call, response: Response) {
                response.use { res ->
                    if (res.code == 200) {
                        val bytes = res.body?.bytes()
                        if (bytes != null && bytes.isNotEmpty()) {
                            val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                            if (bmp != null) {
                                mainHandler.post {
                                    hasCustomWallpaper = true
                                    ivCustomBg.setImageBitmap(bmp); ivCustomBg.visibility = View.VISIBLE
                                    customBgScrim.visibility = View.VISIBLE
                                    applyTheme()
                                }
                                return
                            }
                        }
                    }
                    mainHandler.post { clearCustomWallpaper() }
                }
            }
        })
    }

    private fun clearCustomWallpaper() {
        hasCustomWallpaper = false
        ivCustomBg.setImageBitmap(null); ivCustomBg.visibility = View.GONE; customBgScrim.visibility = View.GONE
        applyTheme()
    }

    private fun triggerUsbSelfUpdate() {
        btnUpdateApp.text = "⏳ ..."; btnUpdateApp.isEnabled = false
        val req = Request.Builder().url(updateApkUrl).post("{}".toRequestBody("application/json; charset=utf-8".toMediaType())).build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) {
                mainHandler.post { btnUpdateApp.text = "⬇ Обновить"; btnUpdateApp.isEnabled = true }
            }
            override fun onResponse(call: okhttp3.Call, response: Response) {
                val txt = response.body?.string() ?: ""; response.close()
                mainHandler.post {
                    btnUpdateApp.text = "⬇ Обновить"; btnUpdateApp.isEnabled = true
                    Toast.makeText(this@MainActivity, if (txt.contains("\"ok\":true")) "Обновлено!" else "Ошибка APK", Toast.LENGTH_SHORT).show()
                }
            }
        })
    }

    private fun applyMediaOrientationLayout() {
        val isLandscape = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        mediaBody.orientation = if (isLandscape) LinearLayout.HORIZONTAL else LinearLayout.VERTICAL
        val rp = mediaRightCol.layoutParams as LinearLayout.LayoutParams
        if (isLandscape) { rp.width = 0; rp.weight = 1f } else { rp.width = LinearLayout.LayoutParams.MATCH_PARENT; rp.weight = 0f }
        mediaRightCol.layoutParams = rp
    }

    private fun setupBouncyMediaBtn(view: View, action: String) {
        view.setOnClickListener { v ->
            v.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
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
            mediaOverlay.visibility = View.VISIBLE; mediaOverlay.translationY = h
            mediaOverlay.animate().translationY(0f).setDuration(240).start()
        } else {
            mediaOverlay.animate().translationY(h).setDuration(200).withEndAction { mediaOverlay.visibility = View.GONE }.start()
        }
    }

    private fun showMacroOverlay(show: Boolean) {
        if (show == isMacroOpen) return
        isMacroOpen = show
        val w = window.decorView.width.toFloat().coerceAtLeast(800f)
        if (show) {
            macroOverlay.visibility = View.VISIBLE; macroOverlay.translationX = w
            macroOverlay.animate().translationX(0f).setDuration(220).start()
        } else {
            macroOverlay.animate().translationX(w).setDuration(200).withEndAction { macroOverlay.visibility = View.GONE }.start()
        }
    }

    private fun sendPost(url: String, json: JSONObject) {
        val req = Request.Builder().url(url).post(json.toString().toRequestBody("application/json; charset=utf-8".toMediaType())).build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) {}
            override fun onResponse(call: okhttp3.Call, response: Response) { response.close() }
        })
    }

    private fun fetchAlbumArt() {
        val req = Request.Builder().url(mediaArtUrl).build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) { mainHandler.post { clearAlbumArt() } }
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
                                    currentAlbumBmp = bmp
                                    ivMediaArt.setImageBitmap(bmp); ivMediaArt.visibility = View.VISIBLE
                                    ivMediaBackdrop.setImageBitmap(blurred)
                                    applyMediaAccent(vibrant)
                                    layoutBlocks.filter { it.type == "media_player" }.forEach { b ->
                                        blockViews[b.id]?.findViewById<ImageView>(R.id.ivBlkArt)?.setImageBitmap(bmp)
                                    }
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
        currentAlbumBmp = null
        ivMediaArt.visibility = View.GONE
        ivMediaBackdrop.setImageBitmap(null)
        applyMediaAccent((themes[currentTheme] ?: themes["midnight"]!!).accent)
        layoutBlocks.filter { it.type == "media_player" }.forEach { b ->
            blockViews[b.id]?.findViewById<ImageView>(R.id.ivBlkArt)?.setImageBitmap(null)
        }
    }

    private fun applyMediaAccent(color: Int) {
        mediaAccentColor = color
        tvMediaStatus.setTextColor(color)
        audioVisualizer.setAccentColor(color)
        mediaProgress.progressTintList = ColorStateList.valueOf(color)
        mediaProgress.progressBackgroundTintList = ColorStateList.valueOf(Color.parseColor("#33FFFFFF"))
        btnPlayPause.background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }
        layoutBlocks.filter { it.type == "media_player" }.forEach { b ->
            val v = blockViews[b.id] ?: return@forEach
            v.findViewById<ProgressBar>(R.id.pbBlkMedia)?.progressTintList = ColorStateList.valueOf(color)
            v.findViewById<ImageView>(R.id.btnBlkPlay)?.background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(color) }
        }
    }

    private fun extractVibrantColor(bmp: Bitmap): Int {
        val small = Bitmap.createScaledBitmap(bmp, 16, 16, true)
        var bestColor = Color.parseColor("#38BDF8"); var bestScore = -1f
        val hsv = FloatArray(3)
        for (y in 0 until 16) for (x in 0 until 16) {
            Color.colorToHSV(small.getPixel(x, y), hsv)
            if (hsv[2] > 0.28f && hsv[1] > 0.25f) {
                val sc = hsv[1] * 1.4f + hsv[2] * 0.6f
                if (sc > bestScore) { bestScore = sc; hsv[2] = hsv[2].coerceIn(0.68f, 0.96f); hsv[1] = hsv[1].coerceIn(0.50f, 0.92f); bestColor = Color.HSVToColor(hsv) }
            }
        }
        small.recycle(); return bestColor
    }

    private fun fastBlur(src: Bitmap, radius: Int): Bitmap {
        val w = 48; val h = 48
        val bmp = Bitmap.createScaledBitmap(src, w, h, true)
        val pix = IntArray(w * h); bmp.getPixels(pix, 0, w, 0, 0, w, h)
        val tmp = IntArray(w * h); val r = radius.coerceIn(2, 16)
        repeat(2) {
            for (y in 0 until h) for (x in 0 until w) {
                var rs = 0; var gs = 0; var bs = 0; var cnt = 0
                for (dx in -r..r) { val c = pix[y * w + (x + dx).coerceIn(0, w - 1)]; rs += Color.red(c); gs += Color.green(c); bs += Color.blue(c); cnt++ }
                tmp[y * w + x] = Color.rgb(rs / cnt, gs / cnt, bs / cnt)
            }
            for (y in 0 until h) for (x in 0 until w) {
                var rs = 0; var gs = 0; var bs = 0; var cnt = 0
                for (dy in -r..r) { val c = tmp[(y + dy).coerceIn(0, h - 1) * w + x]; rs += Color.red(c); gs += Color.green(c); bs += Color.blue(c); cnt++ }
                pix[y * w + x] = Color.rgb(rs / cnt, gs / cnt, bs / cnt)
            }
        }
        val out = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
        out.setPixels(pix, 0, w, 0, 0, w, h); bmp.recycle(); return out
    }

    private fun formatTime(sec: Double?): String {
        if (sec == null || sec.isNaN() || sec < 0) return "0:00"
        val total = sec.roundToInt()
        return "%d:%02d".format(total / 60, total % 60)
    }

    private fun macroAccentFor(idx: Int, label: String): Int {
        val low = label.lowercase()
        return when {
            "запис" in low || "стрим" in low || "rec" in low -> Color.parseColor("#F85149")
            "мут" in low || "мик" in low || "mic" in low -> Color.parseColor("#FF7B72")
            "сцен" in low || "scene" in low -> Color.parseColor("#A371F7")
            "скрин" in low || "shot" in low -> Color.parseColor("#3FB950")
            "звук" in low || "vol" in low -> Color.parseColor("#D29922")
            else -> listOf("#58A6FF", "#A371F7", "#3FB950", "#FF8A50", "#F85149", "#2DD4BF")[idx % 6].let { Color.parseColor(it) }
        }
    }

    private fun splitIconAndTitle(raw: String): Pair<String, String> {
        val trimmed = raw.trim(); val sp = trimmed.indexOf(' ')
        return if (sp in 1..4) trimmed.substring(0, sp) to trimmed.substring(sp + 1).trim() else "⚡" to trimmed
    }

    private fun rebuildMacroGrid() {
        macroGrid.removeAllViews()
        val th = themes[currentTheme] ?: themes["midnight"]!!
        val isLandscape = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        val count = macros.size.coerceAtLeast(1)
        val cols = if (isLandscape) 3 else 2
        val rows = ceil(count.toDouble() / cols).toInt().coerceAtLeast(1)
        macroGrid.columnCount = cols; macroGrid.rowCount = rows

        val inflater = LayoutInflater.from(this)
        macros.forEachIndexed { idx, m ->
            val tile = inflater.inflate(R.layout.item_macro_tile, macroGrid, false)
            val accent = macroAccentFor(idx, m.label)
            val (icon, title) = splitIconAndTitle(m.label)
            val softBg = Color.argb(38, Color.red(accent), Color.green(accent), Color.blue(accent))
            val subtleBorder = Color.argb(105, Color.red(accent), Color.green(accent), Color.blue(accent))
            val normalBg = GradientDrawable(
                GradientDrawable.Orientation.TL_BR,
                intArrayOf(
                    Color.argb(255, ((Color.red(th.card) * 4 + Color.red(accent)) / 5), ((Color.green(th.card) * 4 + Color.green(accent)) / 5), ((Color.blue(th.card) * 4 + Color.blue(accent)) / 5)),
                    th.card
                )
            ).apply { cornerRadius = 38f; setStroke(2, subtleBorder) }
            val flashBg = GradientDrawable().apply { setColor(Color.argb(95, Color.red(accent), Color.green(accent), Color.blue(accent))); cornerRadius = 38f; setStroke(3, accent) }

            tile.background = normalBg
            tile.findViewById<TextView>(R.id.tvMacroIcon).apply { text = icon; background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(softBg); setStroke(2, subtleBorder) } }
            tile.findViewById<TextView>(R.id.tvMacroTitle).text = title
            tile.findViewById<TextView>(R.id.tvMacroHotkey).apply {
                text = m.hotkey; setTextColor(accent)
                background = GradientDrawable().apply { setColor(Color.parseColor("#22000000")); cornerRadius = 18f; setStroke(1, Color.argb(70, Color.red(accent), Color.green(accent), Color.blue(accent))) }
            }
            tile.setOnClickListener { v ->
                v.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                v.background = flashBg
                v.animate().scaleX(0.92f).scaleY(0.92f).setDuration(80).withEndAction {
                    v.animate().scaleX(1f).scaleY(1f).setDuration(120).withEndAction { v.background = normalBg }.start()
                }.start()
                sendPost(macroUrl, JSONObject().put("hotkey", m.hotkey))
            }
            tile.layoutParams = GridLayout.LayoutParams(GridLayout.spec(idx / cols, 1f), GridLayout.spec(idx % cols, 1f)).apply { width = 0; height = 0; setMargins(10, 10, 10, 10) }
            macroGrid.addView(tile)
        }
    }

    private fun updateObsBar(d: JSONObject) {
        val connected = d.optBoolean("obsConnected", false)
        obsBar.visibility = if (connected) View.VISIBLE else View.GONE
        if (!connected) return
        val streaming = d.optBoolean("obsStreaming", false)
        val recording = d.optBoolean("obsRecording", false)
        val tc = if (d.isNull("obsStreamTime")) "" else d.optString("obsStreamTime", "")
        val curScene = if (d.isNull("obsCurrentScene")) "" else d.optString("obsCurrentScene", "")
        btnObsStream.text = if (streaming) "🔴 LIVE $tc" else "○ OBS Стрим"
        btnObsStream.setTextColor(if (streaming) Color.parseColor("#FF7B72") else Color.parseColor("#E6EDF3"))
        btnObsRecord.text = if (recording) "🔴 Идёт запись" else "⏺ Запись"
        btnObsRecord.setTextColor(if (recording) Color.parseColor("#FF7B72") else Color.parseColor("#E6EDF3"))

        val scenesArr = d.optJSONArray("obsScenes") ?: return
        val scenes = mutableListOf<String>()
        for (i in 0 until scenesArr.length()) scenes.add(scenesArr.optString(i))
        val key = "$curScene|${scenes.joinToString(",")}"
        if (key == lastObsScenesKey) return
        lastObsScenesKey = key

        obsScenesRow.removeAllViews()
        scenes.forEach { name ->
            val active = name == curScene
            val pill = TextView(this).apply {
                text = name; setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
                setTextColor(if (active) Color.parseColor("#090C10") else Color.parseColor("#C9D1D9"))
                setPadding(26, 12, 26, 12)
                background = GradientDrawable().apply { cornerRadius = 32f; setColor(if (active) Color.parseColor("#38BDF8") else Color.parseColor("#1B222C")); setStroke(1, Color.parseColor("#30363D")) }
                setOnClickListener { v -> v.performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY); sendPost(obsUrl, JSONObject().put("action", "switch_scene").put("sceneName", name)) }
            }
            pill.layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT).apply { marginEnd = 12 }
            obsScenesRow.addView(pill)
        }
    }

    override fun onResume() { super.onResume(); applyImmersiveMode() }
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        rebuildBentoGrid(); rebuildMacroGrid(); applyMediaOrientationLayout()
        lastPayload?.let { updateUi(it) }
    }
    override fun onDestroy() {
        isDestroyedFlag = true
        mainHandler.removeCallbacks(clockAndShiftRunnable)
        webSocket?.close(1000, "Destroyed")
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
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN
                    or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                    or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
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

    private fun connectWebSocket() {
        if (isDestroyedFlag) return
        val request = Request.Builder().url(wsUrl).build()
        webSocket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                mainHandler.post { tvStatus.text = "● USB"; tvStatus.setTextColor(Color.parseColor("#3FB950")) }
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                try { val json = JSONObject(text); lastPayload = json; mainHandler.post { updateUi(json) } } catch (_: Exception) {}
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = scheduleReconnect()
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = scheduleReconnect()
        })
    }

    private fun scheduleReconnect() {
        if (isDestroyedFlag) return
        mainHandler.post { tvStatus.text = "○ USB..."; tvStatus.setTextColor(Color.parseColor("#D29922")) }
        mainHandler.postDelayed({ connectWebSocket() }, 2000)
    }

    private fun updateUi(d: JSONObject) {
        alertPulseToggle = !alertPulseToggle
        btnUpdateApp.visibility = if (d.optBoolean("pcApkAvailable", false)) View.VISIBLE else View.GONE
        btnPreset.text = when (d.optString("activePreset", "tiktok")) {
            "gamer" -> "🎮 Игра"
            "streamer" -> "📺 Стрим"
            "oc" -> "🔥 Разгон"
            else -> "✨ Главный"
        }

        val wpVer = d.optInt("wallpaperVersion", 0)
        if (wpVer != lastWallpaperVersion) {
            lastWallpaperVersion = wpVer
            if (wpVer > 0) fetchCustomWallpaper() else clearCustomWallpaper()
        }

        // Sync Bento Layout from PC Studio
        val rCols = d.optInt("layoutCols", layoutCols).coerceIn(2, 6)
        val rRows = d.optInt("layoutRows", layoutRows).coerceIn(2, 5)
        val rBlocksArr = d.optJSONArray("layoutBlocks")
        if (rBlocksArr != null && rBlocksArr.length() > 0) {
            val sig = "$rCols:$rRows:${rBlocksArr}"
            if (sig != lastLayoutSignature) {
                lastLayoutSignature = sig
                layoutCols = rCols; layoutRows = rRows
                val list = mutableListOf<BentoBlock>()
                for (i in 0 until rBlocksArr.length()) {
                    val o = rBlocksArr.optJSONObject(i) ?: continue
                    list.add(BentoBlock(
                        id = o.optString("id", "b$i"),
                        type = o.optString("type", "cpu"),
                        x = o.optInt("x", 0), y = o.optInt("y", 0),
                        w = o.optInt("w", 1), h = o.optInt("h", 1),
                        colorHex = o.optString("color", "#38BDF8")
                    ))
                }
                if (list.isNotEmpty()) { layoutBlocks = list; rebuildBentoGrid() }
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
                val o = remoteMacros.optJSONObject(i) ?: continue
                parsed.add(MacroItem(o.optString("label", "Кнопка"), o.optString("hotkey", "")))
            }
            if (parsed.isNotEmpty() && parsed != macros) { macros = parsed; rebuildMacroGrid() }
        }

        updateObsBar(d)

        val game = d.optString("activeGame", "")
        val hasGame = game.isNotEmpty() && game != "null" && !d.isNull("fps")
        tvGameName.text = if (game.isNotEmpty() && game != "null") "Игра: $game" else ""

        if (hasGame) {
            lastGameSeenMs = System.currentTimeMillis(); manualStandbyOverride = false
            if (isStandbyOpen) setStandbyVisible(false)
        } else if (d.optString("standbyMode", "off") == "auto" && !manualStandbyOverride && !isMediaOpen && !isMacroOpen) {
            if (System.currentTimeMillis() - lastGameSeenMs > 30_000L && !isStandbyOpen) setStandbyVisible(true)
        }

        // Media state
        val mTitle = if (d.isNull("mediaTitle")) "" else d.optString("mediaTitle", "")
        val mArtist = if (d.isNull("mediaArtist")) "" else d.optString("mediaArtist", "")
        val mPlaying = d.optBoolean("mediaPlaying", false)
        val artVer = d.optInt("mediaArtVersion", 0)
        val posSec = optNullableDouble(d, "mediaPosSec")
        val durSec = optNullableDouble(d, "mediaDurSec")
        val progressVal = if (posSec != null && durSec != null && durSec > 0) ((posSec / durSec) * 1000.0).roundToInt().coerceIn(0, 1000) else 0

        if (artVer != lastArtVersion) {
            lastArtVersion = artVer
            if (mTitle.isNotEmpty()) fetchAlbumArt() else clearAlbumArt()
        }
        if (lastMediaPlaying != mPlaying) {
            lastMediaPlaying = mPlaying
            audioVisualizer.setPlaying(mPlaying && mTitle.isNotEmpty())
            val scale = if (mPlaying) 1.0f else 0.90f
            ivMediaArt.animate().scaleX(scale).scaleY(scale).setDuration(240).start()
        }

        if (mTitle.isNotEmpty()) {
            val icon = if (mPlaying) "▶" else "⏸"
            val shortTrack = "$icon $mTitle" + if (mArtist.isNotEmpty()) " — $mArtist" else ""
            tvMiniMedia.text = "⌃ $shortTrack"; tvClockMedia.text = shortTrack
            tvMediaStatus.text = if (mPlaying) "NOW PLAYING" else "PAUSED"
            tvMediaTitle.text = mTitle; tvMediaArtist.text = mArtist
            btnPlayPause.setImageResource(if (mPlaying) R.drawable.ic_pause else R.drawable.ic_play)
            tvMediaPos.text = formatTime(posSec); tvMediaDur.text = formatTime(durSec)
            mediaProgress.progress = progressVal
        } else {
            tvMiniMedia.text = "⌃ Свайп вверх — плеер · ⇠ Свайп влево — кнопки"
            tvClockMedia.text = "Нажми в любом месте — вернуться к датчикам"
            tvMediaStatus.text = "WINDOWS MEDIA"; tvMediaTitle.text = "Ничего не играет"; tvMediaArtist.text = ""
            btnPlayPause.setImageResource(R.drawable.ic_play)
            tvMediaPos.text = "0:00"; tvMediaDur.text = "0:00"; mediaProgress.progress = 0
            audioVisualizer.setPlaying(false); clearAlbumArt()
        }

        // Populate every active Bento Block
        val th = themes[currentTheme] ?: themes["midnight"]!!
        val pcName = d.optString("pcName", "PC")
        val wObj = d.optJSONObject("weather")

        layoutBlocks.forEach { b ->
            val v = blockViews[b.id] ?: return@forEach
            val accent = parseColorSafe(b.colorHex, th.accent)

            when (b.type) {
                "clock_weather" -> {
                    v.findViewById<TextView>(R.id.tvBlockPcStatus)?.text = "● $pcName · USB${getBatteryText()}"
                    if (wObj != null && !wObj.isNull("tempC")) {
                        v.findViewById<TextView>(R.id.tvWeatherIcon)?.text = wObj.optString("icon", "⛅")
                        v.findViewById<TextView>(R.id.tvWeatherTemp)?.text = "${wObj.optDouble("tempC").roundToInt()}°"
                        val desc = wObj.optString("desc", "")
                        val city = wObj.optString("city", "")
                        v.findViewById<TextView>(R.id.tvWeatherDesc)?.text = if (city.isNotEmpty()) "$desc · $city" else desc
                        val mx = if (wObj.isNull("maxC")) "?" else "${wObj.optDouble("maxC").roundToInt()}°"
                        val mn = if (wObj.isNull("minC")) "?" else "${wObj.optDouble("minC").roundToInt()}°"
                        val fl = if (wObj.isNull("feelsLikeC")) "?" else "${wObj.optDouble("feelsLikeC").roundToInt()}°"
                        v.findViewById<TextView>(R.id.tvWeatherSub)?.text = "↑$mx ↓$mn · Ощущается как $fl"
                    }
                }
                "oc_controls" -> {
                    val prof = d.optString("ocProfile", "stock")
                    val status = d.optString("ocStatus", "Сток (0 / 0 МГц)")
                    v.findViewById<TextView>(R.id.tvOcStatus)?.text = status
                    fun styleBtn(id: Int, active: Boolean, colHex: String) {
                        val tv = v.findViewById<TextView>(id) ?: return
                        val col = Color.parseColor(colHex)
                        tv.background = GradientDrawable().apply {
                            cornerRadius = 22f
                            setColor(if (active) Color.argb(90, Color.red(col), Color.green(col), Color.blue(col)) else Color.parseColor("#18202C"))
                            setStroke(if (active) 3 else 1, if (active) col else Color.parseColor("#30363D"))
                        }
                    }
                    styleBtn(R.id.btnOcStock, prof == "stock", "#94A3B8")
                    styleBtn(R.id.btnOcMild, prof == "mild", "#38BDF8")
                    styleBtn(R.id.btnOcStandard, prof == "standard", "#F59E0B")
                    styleBtn(R.id.btnOcExtreme, prof == "extreme", "#F85149")
                }
                "media_player" -> {
                    v.findViewById<TextView>(R.id.tvBlkMediaTitle)?.text = if (mTitle.isNotEmpty()) mTitle else "Ничего не играет"
                    v.findViewById<TextView>(R.id.tvBlkMediaArtist)?.text = if (mArtist.isNotEmpty()) mArtist else "Нажмите ▶ для продолжения"
                    v.findViewById<ImageView>(R.id.btnBlkPlay)?.setImageResource(if (mPlaying) R.drawable.ic_pause else R.drawable.ic_play)
                    v.findViewById<ProgressBar>(R.id.pbBlkMedia)?.progress = progressVal
                }
                else -> bindMetricBlock(v, b, d, accent)
            }
        }
    }

    private fun bindMetricBlock(v: View, b: BentoBlock, d: JSONObject, accent: Int) {
        val tvLbl = v.findViewById<TextView>(R.id.tvBlkLabel) ?: return
        val tvVal = v.findViewById<TextView>(R.id.tvBlkVal) ?: return
        val tvUnit = v.findViewById<TextView>(R.id.tvBlkUnit) ?: return
        val tvSub = v.findViewById<TextView>(R.id.tvBlkSub) ?: return
        val spark = v.findViewById<SparklineView>(R.id.blkSpark) ?: return

        var lbl = b.type.uppercase(); var valStr = "--"; var unit = ""; var sub = ""
        var norm: Float? = null; var critical = false

        when (b.type) {
            "cpu" -> {
                val load = optNullableDouble(d, "cpuLoad"); val temp = optNullableDouble(d, "cpuTemp")
                lbl = "CPU"
                if (load != null) { valStr = load.roundToInt().toString(); unit = "%"; norm = (load / 100.0).toFloat() }
                sub = (if (temp != null) "${temp.roundToInt()}°C · " else "") + d.optString("cpuName", "")
                critical = (temp != null && temp >= 86.0)
            }
            "gpu" -> {
                val load = optNullableDouble(d, "gpuLoad"); val temp = optNullableDouble(d, "gpuTemp")
                val vu = optNullableDouble(d, "gpuVramUsedGb"); val vt = optNullableDouble(d, "gpuVramTotalGb")
                lbl = "GPU"
                if (load != null) { valStr = load.roundToInt().toString(); unit = "%"; norm = (load / 100.0).toFloat() }
                val tStr = if (temp != null) "${temp.roundToInt()}°C" else ""
                val vStr = if (vu != null && vt != null) "${String.format("%.1f", vu)}/${String.format("%.0f", vt)} ГБ" else ""
                sub = listOf(tStr, vStr).filter { it.isNotEmpty() }.joinToString(" · ")
                critical = (temp != null && temp >= 84.0) || (vu != null && vt != null && vt > 0 && vu / vt >= 0.95)
            }
            "ram" -> {
                val pct = optNullableDouble(d, "ramLoad"); val used = optNullableDouble(d, "ramUsedGb"); val total = optNullableDouble(d, "ramTotalGb")
                lbl = if (pct != null) "RAM (${pct.roundToInt()}%)" else "RAM"
                if (used != null) { valStr = String.format("%.1f", used); unit = "ГБ" }
                if (pct != null) norm = (pct / 100.0).toFloat()
                if (used != null && total != null) sub = "${String.format("%.1f", used)} / ${String.format("%.0f", total)} ГБ"
                critical = (pct != null && pct >= 94.0)
            }
            "fps" -> {
                val fps = if (d.isNull("fps")) null else d.optInt("fps")
                val low1 = if (d.isNull("fps1Low")) null else d.optInt("fps1Low")
                lbl = "FPS"
                if (fps != null) { valStr = fps.toString(); unit = "FPS"; norm = fps / 165f; sub = if (low1 != null) "1% low: $low1" else d.optString("activeGame", "") }
                else { sub = if (d.optBoolean("presentMonActive", false)) "Нет игры" else "PresentMon выкл" }
            }
            "network" -> {
                val up = optNullableDouble(d, "netUpMbps"); val down = optNullableDouble(d, "netDownMbps")
                lbl = "NETWORK"
                if (up != null) { valStr = "↑${String.format("%.1f", up)}"; unit = "Мбит"; norm = (up / 50.0).toFloat() }
                if (down != null) sub = "↓ ${String.format("%.1f", down)} Мбит/с"
            }
            "cpu_temp" -> {
                val t = optNullableDouble(d, "cpuTemp"); lbl = "CPU TEMP"
                if (t != null) { valStr = t.roundToInt().toString(); unit = "°C"; norm = (t / 100.0).toFloat(); critical = t >= 86.0 }
                sub = "Tctl / Package"
            }
            "gpu_temp" -> {
                val t = optNullableDouble(d, "gpuTemp"); lbl = "GPU TEMP"
                if (t != null) { valStr = t.roundToInt().toString(); unit = "°C"; norm = (t / 100.0).toFloat(); critical = t >= 84.0 }
                sub = "GPU Core"
            }
            "gpu_vram" -> {
                val vu = optNullableDouble(d, "gpuVramUsedGb"); val vt = optNullableDouble(d, "gpuVramTotalGb"); lbl = "VRAM"
                if (vu != null) { valStr = String.format("%.1f", vu); unit = "ГБ"; if (vt != null && vt > 0) { norm = (vu / vt).toFloat(); sub = "из ${String.format("%.0f", vt)} ГБ"; critical = vu / vt >= 0.95 } }
            }
            "cpu_power" -> {
                val p = optNullableDouble(d, "cpuPower"); lbl = "CPU PWR"
                if (p != null) { valStr = p.roundToInt().toString(); unit = "Вт"; norm = (p / 120.0).toFloat() }
            }
            "gpu_power" -> {
                val p = optNullableDouble(d, "gpuPower"); lbl = "GPU PWR"
                if (p != null) { valStr = p.roundToInt().toString(); unit = "Вт"; norm = (p / 200.0).toFloat() }
            }
            "cpu_clock" -> {
                val c = optNullableDouble(d, "cpuClock"); lbl = "CPU CLK"
                if (c != null) { valStr = c.roundToInt().toString(); unit = "МГц"; norm = (c / 5000.0).toFloat() }
            }
            "gpu_clock" -> {
                val c = optNullableDouble(d, "gpuClock"); lbl = "GPU CLK"
                if (c != null) { valStr = c.roundToInt().toString(); unit = "МГц"; norm = (c / 3000.0).toFloat() }
            }
            "obs_live" -> {
                lbl = "OBS"
                val live = d.optBoolean("obsStreaming", false); val rec = d.optBoolean("obsRecording", false)
                valStr = if (live) "LIVE" else if (rec) "REC" else if (d.optBoolean("obsConnected", false)) "READY" else "OFF"
                sub = if (!d.isNull("obsStreamTime")) d.optString("obsStreamTime", "") else d.optString("obsCurrentScene", "")
                critical = live
            }
        }

        tvLbl.text = lbl; tvLbl.setTextColor(accent)
        tvVal.text = valStr; tvUnit.text = unit; tvSub.text = sub
        if (norm != null) spark.addPoint(norm, accent)
        v.background = makeGlassBlockBg(accent, critical && alertPulseToggle)
    }

    private fun optNullableDouble(d: JSONObject, key: String): Double? {
        if (!d.has(key) || d.isNull(key)) return null
        val v = d.optDouble(key, Double.NaN)
        return if (v.isNaN()) null else v
    }
}
