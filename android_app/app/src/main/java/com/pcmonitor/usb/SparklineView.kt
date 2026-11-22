package com.pcmonitor.usb

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.Shader
import android.util.AttributeSet
import android.view.View

class SparklineView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    private val values = ArrayDeque<Float>()
    private val maxPoints = 45

    private var lineColor = Color.parseColor("#58A6FF")

    private val linePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 3.5f
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }

    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }

    private val linePath = Path()
    private val fillPath = Path()

    fun addPoint(normalized: Float, color: Int) {
        val clamped = normalized.coerceIn(0f, 1f)
        if (values.size >= maxPoints) values.removeFirst()
        values.addLast(clamped)
        lineColor = color
        invalidate()
    }

    fun clearPoints() {
        values.clear()
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (values.size < 2 || width <= 0 || height <= 0) return

        val w = width.toFloat()
        val h = height.toFloat()
        val pad = 4f
        val usableH = (h - pad * 2).coerceAtLeast(1f)
        val stepX = w / (values.size - 1)

        linePath.reset()
        fillPath.reset()

        values.forEachIndexed { idx, v ->
            val x = idx * stepX
            val y = h - pad - v * usableH
            if (idx == 0) {
                linePath.moveTo(x, y)
                fillPath.moveTo(x, h)
                fillPath.lineTo(x, y)
            } else {
                linePath.lineTo(x, y)
                fillPath.lineTo(x, y)
            }
        }

        fillPath.lineTo(w, h)
        fillPath.close()

        val topAlpha = Color.argb(65, Color.red(lineColor), Color.green(lineColor), Color.blue(lineColor))
        fillPaint.shader = LinearGradient(0f, 0f, 0f, h, topAlpha, Color.TRANSPARENT, Shader.TileMode.CLAMP)
        linePaint.color = lineColor

        canvas.drawPath(fillPath, fillPaint)
        canvas.drawPath(linePath, linePaint)
    }
}
