package com.pcmonitor.usb

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.View
import kotlin.math.PI
import kotlin.math.atan2
import kotlin.math.cos
import kotlin.math.hypot
import kotlin.math.min
import kotlin.math.sin

class RadialMenuView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    data class Sector(val id: String, val icon: String, val label: String, val color: Int)

    var sectors: List<Sector> = listOf(
        Sector("preset_tiktok",   "✨", "Главный", Color.parseColor("#38BDF8")),
        Sector("preset_gauges",   "🌀", "Кольца",  Color.parseColor("#22D3EE")),
        Sector("preset_gamer",    "🎮", "Игра",    Color.parseColor("#22C55E")),
        Sector("preset_streamer", "📺", "Стрим",   Color.parseColor("#A78BFA")),
        Sector("preset_oc",       "🔥", "Разгон",  Color.parseColor("#F97316")),
        Sector("open_mixer",      "🎚", "Микшер",  Color.parseColor("#FB923C")),
        Sector("open_macros",     "⚡", "Кнопки",  Color.parseColor("#F43F5E")),
        Sector("open_media",      "🎵", "Плеер",   Color.parseColor("#60A5FA"))
    )

    var onSectorSelected: ((String) -> Unit)? = null
    var onDismiss: (() -> Unit)? = null

    private var hoveredIndex = -1
    private val bgPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#CC05080F") }
    private val sectorPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }
    private val strokePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 3f
        color = Color.parseColor("#44FFFFFF")
    }
    private val hubPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.parseColor("#131C2B") }
    private val iconPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        textAlign = Paint.Align.CENTER
        textSize = 46f
    }
    private val labelPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.parseColor("#E2E8F0")
        textAlign = Paint.Align.CENTER
        textSize = 24f
        isFakeBoldText = true
    }
    private val hubTextPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        textAlign = Paint.Align.CENTER
        textSize = 26f
        isFakeBoldText = true
    }

    private val outerRect = RectF()

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (width <= 0 || height <= 0) return

        canvas.drawRect(0f, 0f, width.toFloat(), height.toFloat(), bgPaint)

        val cx = width / 2f
        val cy = height / 2f
        val outerR = min(width, height) * 0.44f
        val innerR = outerR * 0.34f
        val midR = (outerR + innerR) / 2f
        val sweep = 360f / sectors.size

        outerRect.set(cx - outerR, cy - outerR, cx + outerR, cy + outerR)

        sectors.forEachIndexed { idx, s ->
            val startDeg = -90f - sweep / 2f + idx * sweep
            val isHot = idx == hoveredIndex

            sectorPaint.color = if (isHot) {
                Color.argb(220, Color.red(s.color), Color.green(s.color), Color.blue(s.color))
            } else {
                Color.argb(175, (18 + Color.red(s.color) / 6), (25 + Color.green(s.color) / 6), (38 + Color.blue(s.color) / 6))
            }
            canvas.drawArc(outerRect, startDeg, sweep - 2f, true, sectorPaint)

            strokePaint.color = if (isHot) s.color else Color.argb(90, Color.red(s.color), Color.green(s.color), Color.blue(s.color))
            strokePaint.strokeWidth = if (isHot) 5f else 2.5f
            canvas.drawArc(outerRect, startDeg, sweep - 2f, true, strokePaint)

            val rad = Math.toRadians((startDeg + sweep / 2f).toDouble())
            val tx = cx + (cos(rad) * midR).toFloat()
            val ty = cy + (sin(rad) * midR).toFloat()

            canvas.drawText(s.icon, tx, ty - 2f, iconPaint)
            canvas.drawText(s.label, tx, ty + 28f, labelPaint)
        }

        // Center hub
        canvas.drawCircle(cx, cy, innerR, hubPaint)
        strokePaint.color = Color.parseColor("#38BDF8")
        strokePaint.strokeWidth = 4f
        canvas.drawCircle(cx, cy, innerR, strokePaint)
        canvas.drawText("✕", cx, cy + 9f, hubTextPaint)
    }

    override fun onTouchEvent(event: MotionEvent): Boolean {
        val cx = width / 2f
        val cy = height / 2f
        val outerR = min(width, height) * 0.44f
        val innerR = outerR * 0.34f
        val dx = event.x - cx
        val dy = event.y - cy
        val dist = hypot(dx.toDouble(), dy.toDouble()).toFloat()

        val idx = if (dist in innerR..(outerR * 1.15f)) {
            val sweep = 360f / sectors.size
            var deg = Math.toDegrees(atan2(dy.toDouble(), dx.toDouble())).toFloat() + 90f + sweep / 2f
            while (deg < 0f) deg += 360f
            while (deg >= 360f) deg -= 360f
            (deg / sweep).toInt().coerceIn(0, sectors.size - 1)
        } else -1

        when (event.action) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE -> {
                if (idx != hoveredIndex) {
                    hoveredIndex = idx
                    if (idx >= 0) performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                    invalidate()
                }
            }
            MotionEvent.ACTION_UP -> {
                if (idx >= 0) {
                    performHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
                    onSectorSelected?.invoke(sectors[idx].id)
                } else {
                    onDismiss?.invoke()
                }
                hoveredIndex = -1
                invalidate()
            }
            MotionEvent.ACTION_CANCEL -> {
                hoveredIndex = -1
                invalidate()
            }
        }
        return true
    }
}
