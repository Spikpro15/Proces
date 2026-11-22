package com.pcmonitor.usb

import android.app.AlertDialog
import android.content.pm.ActivityInfo
import android.content.res.ColorStateList
import android.content.res.Configuration
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.GestureDetector
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.GridLayout
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

    private lateinit var gestureDetector: GestureDetector
    private lateinit var tvStatus: TextView
    private lateinit var tvGameName: TextView
    private lateinit var metricsGrid: GridLayout
    private lateinit var tvMiniMedia: TextView
    private lateinit var mediaOverlay: View
    private lateinit var tvMediaStatus: TextView
    private lateinit var tvMediaTitle: TextView
    private lateinit var tvMediaArtist: TextView
    private lateinit var btnPlayPause: TextView

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

    private data class CardHolder(
        val tvLabel: TextView,
        val tvValue: TextView,
        val tvUnit: TextView,
        val tvSub: TextView,
        val progress: ProgressBar
    )

    private val cards = mutableMapOf<String, CardHolder>()
    private var lastPayload: JSONObject? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_main)

        tvStatus = findViewById(R.id.tvStatus)
        tvGameName = findViewById(R.id.tvGameName)
        metricsGrid = findViewById(R.id.metricsGrid)
        tvMiniMedia = findViewById(R.id.tvMiniMedia)
        mediaOverlay = findViewById(R.id.mediaOverlay)
        tvMediaStatus = findViewById(R.id.tvMediaStatus)
        tvMediaTitle = findViewById(R.id.tvMediaTitle)
        tvMediaArtist = findViewById(R.id.tvMediaArtist)
        btnPlayPause = findViewById(R.id.btnPlayPause)

        findViewById<View>(R.id.btnSettings).setOnClickListener { openSettingsDialog() }
        findViewById<View>(R.id.btnRotate).setOnClickListener { cycleOrientation() }
        findViewById<View>(R.id.btnDim).setOnClickListener { toggleDim() }
        tvMiniMedia.setOnClickListener { showMediaOverlay(true) }
        findViewById<View>(R.id.btnCloseMedia).setOnClickListener { showMediaOverlay(false) }

        findViewById<View>(R.id.btnPrev).setOnClickListener { sendMediaCommand("prev") }
        btnPlayPause.setOnClickListener { sendMediaCommand("play_pause") }
        findViewById<View>(R.id.btnNext).setOnClickListener { sendMediaCommand("next") }
        findViewById<View>(R.id.btnVolDown).setOnClickListener { sendMediaCommand("vol_down") }
        findViewById<View>(R.id.btnVolMute).setOnClickListener { sendMediaCommand("vol_mute") }
        findViewById<View>(R.id.btnVolUp).setOnClickListener { sendMediaCommand("vol_up") }

        gestureDetector = GestureDetector(this, object : GestureDetector.SimpleOnGestureListener() {
            override fun onFling(
                e1: MotionEvent?,
                e2: MotionEvent,
                velocityX: Float,
                velocityY: Float
            ): Boolean {
                if (e1 == null) return false
                val diffY = e2.y - e1.y
                val diffX = e2.x - e1.x
                if (abs(diffY) > abs(diffX) && abs(diffY) > 90 && abs(velocityY) > 120) {
                    if (diffY < 0 && !isMediaOpen) {
                        showMediaOverlay(true)
                        return true
                    } else if (diffY > 0 && isMediaOpen) {
                        showMediaOverlay(false)
                        return true
                    }
                }
                return false
            }
        })

        loadSavedMetrics()
        rebuildGrid()
        applyImmersiveMode()
        connectWebSocket()
    }

    override fun dispatchTouchEvent(ev: MotionEvent): Boolean {
        gestureDetector.onTouchEvent(ev)
        return super.dispatchTouchEvent(ev)
    }

    private fun showMediaOverlay(show: Boolean) {
        if (show == isMediaOpen) return
        isMediaOpen = show
        val height = window.decorView.height.toFloat().coerceAtLeast(800f)
        if (show) {
            mediaOverlay.visibility = View.VISIBLE
            mediaOverlay.translationY = height
            mediaOverlay.animate().translationY(0f).setDuration(220).start()
        } else {
            mediaOverlay.animate().translationY(height).setDuration(200).withEndAction {
                mediaOverlay.visibility = View.GONE
            }.start()
        }
    }

    private fun sendMediaCommand(action: String) {
        val bodyJson = JSONObject().put("action", action).toString()
        val req = Request.Builder()
            .url(mediaUrl)
            .post(bodyJson.toRequestBody("application/json; charset=utf-8".toMediaType()))
            .build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) {}
            override fun onResponse(call: okhttp3.Call, response: Response) { response.close() }
        })
    }

    override fun onResume() {
        super.onResume()
        applyImmersiveMode()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        rebuildGrid()
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
                ctrl.systemBarsBehavior =
                    WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
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
        if (list.isNotEmpty()) {
            enabledMetrics = list.toMutableList()
        }
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
                tvLabel = card.findViewById(R.id.tvLabel),
                tvValue = card.findViewById(R.id.tvValue),
                tvUnit = card.findViewById(R.id.tvUnit),
                tvSub = card.findViewById(R.id.tvSub),
                progress = card.findViewById(R.id.progressBar)
            )
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

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                scheduleReconnect()
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                scheduleReconnect()
            }
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

        val game = d.optString("activeGame", "")
        tvGameName.text = if (game.isNotEmpty() && game != "null") "Игра: $game" else ""

        // Update Media Player Overlay + bottom hint
        val mTitle = if (d.isNull("mediaTitle")) "" else d.optString("mediaTitle", "")
        val mArtist = if (d.isNull("mediaArtist")) "" else d.optString("mediaArtist", "")
        val mPlaying = d.optBoolean("mediaPlaying", false)
        if (mTitle.isNotEmpty()) {
            val icon = if (mPlaying) "▶" else "⏸"
            tvMiniMedia.text = "⌃ $icon $mTitle" + if (mArtist.isNotEmpty()) " — $mArtist" else ""
            tvMediaStatus.text = if (mPlaying) "▶ Сейчас играет" else "⏸ На паузе"
            tvMediaTitle.text = mTitle
            tvMediaArtist.text = mArtist
            btnPlayPause.text = if (mPlaying) "⏸" else "▶"
        } else {
            tvMiniMedia.text = "⌃ Свайп вверх — плеер Windows"
            tvMediaStatus.text = "Плеер Windows"
            tvMediaTitle.text = "Ничего не играет"
            tvMediaArtist.text = ""
            btnPlayPause.text = "⏯"
        }

        cards["fps"]?.let { c ->
            val fps = if (d.isNull("fps")) null else d.optInt("fps")
            val low1 = if (d.isNull("fps1Low")) null else d.optInt("fps1Low")
            if (fps != null) {
                c.tvValue.text = fps.toString()
                c.tvUnit.text = "FPS"
                c.tvSub.text = if (low1 != null) "1% low: $low1 FPS" else ""
                val color = when {
                    fps >= 60 -> "#3FB950"
                    fps >= 30 -> "#D29922"
                    else -> "#F85149"
                }
                setCardAccent(c, color, (fps * 100 / 165).coerceIn(0, 100))
            } else {
                c.tvValue.text = "--"
                c.tvUnit.text = "FPS"
                c.tvSub.text = if (d.optBoolean("presentMonActive", false)) "Нет активной игры" else "PresentMon не запущен"
                setCardAccent(c, "#8B949E", 0)
            }
        }

        updatePercentCard("cpu_load", d, "cpuLoad", d.optString("cpuName", ""))
        updateTempCard("cpu_temp", d, "cpuTemp", "Tctl / Package")
        updatePercentCard("gpu_load", d, "gpuLoad", d.optString("gpuName", ""))
        updateTempCard("gpu_temp", d, "gpuTemp", "GPU Core")

        cards["ram_load"]?.let { c ->
            val pct = optNullableDouble(d, "ramLoad")
            val used = optNullableDouble(d, "ramUsedGb")
            val total = optNullableDouble(d, "ramTotalGb")
            if (pct != null) {
                c.tvValue.text = pct.roundToInt().toString()
                c.tvUnit.text = "%"
                c.tvSub.text = if (used != null && total != null) "$used / $total ГБ" else ""
                setCardAccent(c, loadColor(pct), pct.roundToInt().coerceIn(0, 100))
            } else {
                c.tvValue.text = "--"; c.tvUnit.text = "%"; c.tvSub.text = ""; setCardAccent(c, "#8B949E", 0)
            }
        }

        cards["gpu_vram"]?.let { c ->
            val used = optNullableDouble(d, "gpuVramUsedGb")
            val total = optNullableDouble(d, "gpuVramTotalGb")
            if (used != null) {
                c.tvValue.text = String.format("%.1f", used)
                c.tvUnit.text = "ГБ"
                val pct = if (total != null && total > 0) ((used / total) * 100).roundToInt() else 0
                c.tvSub.text = if (total != null) "из ${String.format("%.1f", total)} ГБ ($pct%)" else ""
                setCardAccent(c, loadColor(pct.toDouble()), pct.coerceIn(0, 100))
            } else {
                c.tvValue.text = "--"; c.tvUnit.text = "ГБ"; c.tvSub.text = ""; setCardAccent(c, "#8B949E", 0)
            }
        }

        updateSimpleCard("cpu_power", d, "cpuPower", "Вт", 120.0, "#58A6FF")
        updateSimpleCard("gpu_power", d, "gpuPower", "Вт", 200.0, "#A371F7")
        updateSimpleCard("cpu_clock", d, "cpuClock", "МГц", 5000.0, "#58A6FF")
        updateSimpleCard("gpu_clock", d, "gpuClock", "МГц", 3000.0, "#A371F7")
    }

    private fun updatePercentCard(id: String, d: JSONObject, key: String, sub: String) {
        val c = cards[id] ?: return
        val v = optNullableDouble(d, key)
        if (v != null) {
            c.tvValue.text = v.roundToInt().toString()
            c.tvUnit.text = "%"
            c.tvSub.text = sub
            setCardAccent(c, loadColor(v), v.roundToInt().coerceIn(0, 100))
        } else {
            c.tvValue.text = "--"; c.tvUnit.text = "%"; c.tvSub.text = ""; setCardAccent(c, "#8B949E", 0)
        }
    }

    private fun updateTempCard(id: String, d: JSONObject, key: String, sub: String) {
        val c = cards[id] ?: return
        val v = optNullableDouble(d, key)
        if (v != null) {
            c.tvValue.text = v.roundToInt().toString()
            c.tvUnit.text = "°C"
            c.tvSub.text = sub
            val color = when {
                v >= 85.0 -> "#F85149"
                v >= 72.0 -> "#D29922"
                else -> "#3FB950"
            }
            setCardAccent(c, color, v.roundToInt().coerceIn(0, 100))
        } else {
            c.tvValue.text = "--"; c.tvUnit.text = "°C"; c.tvSub.text = ""; setCardAccent(c, "#8B949E", 0)
        }
    }

    private fun updateSimpleCard(id: String, d: JSONObject, key: String, unit: String, maxVal: Double, hex: String) {
        val c = cards[id] ?: return
        val v = optNullableDouble(d, key)
        if (v != null) {
            c.tvValue.text = v.roundToInt().toString()
            c.tvUnit.text = unit
            c.tvSub.text = ""
            setCardAccent(c, hex, ((v / maxVal) * 100).roundToInt().coerceIn(0, 100))
        } else {
            c.tvValue.text = "--"; c.tvUnit.text = unit; c.tvSub.text = ""; setCardAccent(c, "#8B949E", 0)
        }
    }

    private fun loadColor(pct: Double): String = when {
        pct >= 90.0 -> "#F85149"
        pct >= 75.0 -> "#D29922"
        else -> "#58A6FF"
    }

    private fun setCardAccent(c: CardHolder, hex: String, progress: Int) {
        val color = Color.parseColor(hex)
        c.tvValue.setTextColor(color)
        c.progress.progress = progress
        c.progress.progressTintList = ColorStateList.valueOf(color)
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
            .setMultiChoiceItems(labels, checked) { _, which, isChecked ->
                checked[which] = isChecked
            }
            .setPositiveButton("Сохранить") { _, _ ->
                val selected = allMetricIds.filterIndexed { idx, _ -> checked[idx] }
                if (selected.isNotEmpty()) {
                    enabledMetrics = selected.toMutableList()
                    saveMetricsLocally()
                    rebuildGrid()
                    lastPayload?.let { updateUi(it) }
                    pushConfigToPc(selected)
                }
            }
            .setNegativeButton("Отмена", null)
            .show()
    }

    private fun pushConfigToPc(selected: List<String>) {
        val bodyJson = JSONObject().put("enabledMetrics", JSONArray(selected)).toString()
        val req = Request.Builder()
            .url(configUrl)
            .post(bodyJson.toRequestBody("application/json; charset=utf-8".toMediaType()))
            .build()
        client.newCall(req).enqueue(object : okhttp3.Callback {
            override fun onFailure(call: okhttp3.Call, e: java.io.IOException) {}
            override fun onResponse(call: okhttp3.Call, response: Response) { response.close() }
        })
    }
}
