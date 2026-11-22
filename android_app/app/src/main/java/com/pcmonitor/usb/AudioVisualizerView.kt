package com.pcmonitor.usb

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View
import kotlin.math.sin
import kotlin.random.Random

class AudioVisualizerView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null
) : View(context, attrs) {

    private val barCount = 16
    private val heights = FloatArray(barCount) { 0.15f }
    private val targets = FloatArray(barCount) { 0.15f }
    private val phases = FloatArray(barCount) { it * 0.55f }
    private var playing = false
    private var tick = 0f
    private var accentColor = Color.parseColor("#58A6FF")

    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }
    private val rect = RectF()

    private val frameRunnable = object : Runnable {
        override fun run() {
            stepAnimation()
            invalidate()
            if (playing || heights.any { it > 0.13f }) {
                postDelayed(this, 32L)
            }
        }
    }

    fun setAccentColor(color: Int) {
        accentColor = color
        invalidate()
    }

    fun setPlaying(isPlaying: Boolean) {
        if (playing == isPlaying) return
        playing = isPlaying
        removeCallbacks(frameRunnable)
        post(frameRunnable)
    }

    private fun stepAnimation() {
        tick += 0.22f
        for (i in 0 until barCount) {
            if (playing) {
                val wave = (sin((tick + phases[i]).toDouble()).toFloat() + 1f) * 0.35f
                val centerBoost = 1f - (kotlin.math.abs(i - barCount / 2f) / (barCount / 2f)) * 0.45f
                if (Random.nextFloat() < 0.22f) {
                    targets[i] = (0.2f + wave + Random.nextFloat() * 0.45f) * centerBoost
                }
                heights[i] += (targets[i].coerceIn(0.14f, 1f) - heights[i]) * 0.28f
            } else {
                heights[i] += (0.12f - heights[i]) * 0.22f
            }
        }
    }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        if (width <= 0 || height <= 0) return

        val gap = 6f
        val barW = ((width - gap * (barCount - 1)) / barCount).coerceAtLeast(4f)
        val maxH = height.toFloat()
        val radius = barW / 2f

        for (i in 0 until barCount) {
            val h = (heights[i] * maxH).coerceAtLeast(barW)
            val left = i * (barW + gap)
            val top = (maxH - h) / 2f
            val alpha = (140 + (heights[i] * 115).toInt()).coerceIn(120, 255)
            paint.color = Color.argb(alpha, Color.red(accentColor), Color.green(accentColor), Color.blue(accentColor))
            rect.set(left, top, left + barW, top + h)
            canvas.drawRoundRect(rect, radius, radius, paint)
        }
    }

    override fun onDetachedFromWindow() {
        removeCallbacks(frameRunnable)
        super.onDetachedFromWindow()
    }
}
