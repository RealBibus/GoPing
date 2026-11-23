package com.goping.logging;

import ch.qos.logback.classic.spi.ILoggingEvent;
import ch.qos.logback.classic.spi.IThrowableProxy;
import ch.qos.logback.core.AppenderBase;

import java.time.Instant;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ConcurrentLinkedDeque;

/** Keeps the most recent log lines in memory for the admin logs API. */
public class RingBufferAppender extends AppenderBase<ILoggingEvent> {

    private final ConcurrentLinkedDeque<String> lines = new ConcurrentLinkedDeque<>();
    private volatile int capacity = 400;

    public void setCapacity(int capacity) {
        if (capacity > 0) {
            this.capacity = capacity;
        }
    }

    public int capacity() {
        return capacity;
    }

    @Override
    protected void append(ILoggingEvent event) {
        StringBuilder line = new StringBuilder()
                .append(DateTimeFormatter.ISO_INSTANT.format(Instant.ofEpochMilli(event.getTimeStamp())))
                .append(' ')
                .append(event.getLevel())
                .append(' ')
                .append(event.getLoggerName())
                .append(" - ")
                .append(event.getFormattedMessage());
        IThrowableProxy throwable = event.getThrowableProxy();
        if (throwable != null) {
            line.append(" | ").append(throwable.getClassName());
            if (throwable.getMessage() != null) {
                line.append(": ").append(throwable.getMessage());
            }
        }
        lines.addLast(line.toString());
        while (lines.size() > capacity) {
            lines.pollFirst();
        }
    }

    public List<String> recent(int count) {
        int safeCount = Math.max(1, Math.min(count, capacity));
        List<String> snapshot = new ArrayList<>(lines);
        int from = Math.max(0, snapshot.size() - safeCount);
        return List.copyOf(snapshot.subList(from, snapshot.size()));
    }
}
