package com.goping.config;

import com.goping.error.ValidationException;
import org.springframework.stereotype.Component;

import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.atomic.AtomicReference;

/**
 * Runtime limits. Initialized from AppProperties and updated by the admin API
 * without a restart. No database is involved.
 */
@Component
public class LiveLimits {

    public record Limits(int maxTextLength, int maxVoiceSeconds, int maxCallMinutes, long maxFileBytes) {
    }

    /** Null fields in the request keep their current value. */
    public record Update(Integer maxTextLength, Integer maxVoiceSeconds, Integer maxCallMinutes, Long maxFileBytes) {
    }

    private final AtomicReference<Limits> current;

    public LiveLimits(AppProperties properties) {
        this.current = new AtomicReference<>(new Limits(
                properties.maxTextLength(),
                properties.maxVoiceSeconds(),
                properties.maxCallMinutes(),
                properties.maxFileBytes()));
    }

    public Limits get() {
        return current.get();
    }

    /** Null fields in the request keep their current value. */
    public Limits update(Update request) {
        while (true) {
            Limits old = current.get();
            Limits next = merge(old, request);
            if (current.compareAndSet(old, next)) {
                return next;
            }
        }
    }

    private static Limits merge(Limits old, Update request) {
        if (request == null) {
            return old;
        }
        int text = valueOr(request.maxTextLength(), old.maxTextLength(), "maxTextLength", 1, Integer.MAX_VALUE);
        int voice = valueOr(request.maxVoiceSeconds(), old.maxVoiceSeconds(), "maxVoiceSeconds", 1, Integer.MAX_VALUE);
        int call = valueOr(request.maxCallMinutes(), old.maxCallMinutes(), "maxCallMinutes", 0, Integer.MAX_VALUE);
        long file = valueOr(request.maxFileBytes(), old.maxFileBytes(), "maxFileBytes", 1L, Long.MAX_VALUE);
        return new Limits(text, voice, call, file);
    }

    private static int valueOr(Integer value, int fallback, String field, int min, int max) {
        if (value == null) {
            return fallback;
        }
        if (value < min || value > max) {
            throw invalid(field);
        }
        return value;
    }

    private static long valueOr(Long value, long fallback, String field, long min, long max) {
        if (value == null) {
            return fallback;
        }
        if (value < min || value > max) {
            throw invalid(field);
        }
        return value;
    }

    private static ValidationException invalid(String field) {
        Map<String, String> details = new LinkedHashMap<>();
        details.put(field, "incorrect limit value");
        return new ValidationException(details);
    }
}
