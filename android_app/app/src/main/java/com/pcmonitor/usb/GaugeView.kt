package com.pcmonitor.usb

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View
import kotlin.math.min

class GaugeView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    private var progress = 0f
    private var accentColor = Color.parseColor("#38BDF8")

    private val trackPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 10f
        strokeCap = Paint.Cap.ROUND
        color = Color.parseColor("#26FFFFFF")
    }

    private val arcPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 10f
        strokeCap = Paint.Cap.ROUND
    }

    private val oval = RectF()

    fun setGauge(normalized: Float, color: Int) {
        progress = normalized.coerceIn(0f, 1f)
        accentColor = color
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (width <= 0 || height <= 0) return
        val size = min(width, height).toFloat() - 16f
        if (size <= 10f) return
        val left = (width - size) / 2f
        val top = (height - size) / 2f
        oval.set(left, top, left + size, top + size)

        canvas.drawArc(oval, 135f, 270f, false, trackPaint)
        arcPaint.color = accentColor
        canvas.drawArc(oval, 135f, 270f * progress, false, arcPaint)
    }
}
