package com.aerisflow.messenger.web;

import com.aerisflow.messenger.web.dto.ErrorDtos;
import com.fasterxml.jackson.databind.ObjectMapper;
import jakarta.servlet.http.HttpServletResponse;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;

import java.io.IOException;
import java.nio.charset.StandardCharsets;

/**
 * Запись JSON-ошибки из фильтров (где нет @RestControllerAdvice).
 */
public final class ErrorResponseWriter {

    private ErrorResponseWriter() {
    }

    public static void write(HttpServletResponse response,
                             ObjectMapper mapper,
                             HttpStatus status,
                             String code,
                             String message,
                             Object details,
                             String requestId) throws IOException {
        response.setStatus(status.value());
        response.setCharacterEncoding(StandardCharsets.UTF_8.name());
        response.setContentType(MediaType.APPLICATION_JSON_VALUE);
        mapper.writeValue(response.getOutputStream(), new ErrorDtos.ErrorEnvelope(
                new ErrorDtos.ErrorBody(code, message, details, requestId)
        ));
    }
}
