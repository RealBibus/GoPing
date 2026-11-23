package com.goping.service;

import ch.qos.logback.classic.LoggerContext;
import ch.qos.logback.classic.spi.ILoggingEvent;
import ch.qos.logback.core.Appender;
import com.goping.logging.RingBufferAppender;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Component;

import java.util.List;

/** Reads the in-memory ring appender installed by logback-spring.xml. */
@Component
public class ServerLogBuffer {

    private static final String APPENDER_NAME = "RING";

    public List<String> recent(int lines) {
        int count = Math.max(1, Math.min(lines, 1000));
        if (LoggerFactory.getILoggerFactory() instanceof LoggerContext context) {
            Appender<ILoggingEvent> appender =
                    context.getLogger(Logger.ROOT_LOGGER_NAME).getAppender(APPENDER_NAME);
            if (appender instanceof RingBufferAppender ring) {
                return ring.recent(count);
            }
        }
        return List.of();
    }
}
