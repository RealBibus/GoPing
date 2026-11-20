package com.goping.web;

import com.goping.error.ApiException;
import com.goping.security.RequestContext;
import com.goping.web.dto.ErrorDtos;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.HttpStatus;
import org.springframework.http.ResponseEntity;
import org.springframework.http.converter.HttpMessageNotReadableException;
import org.springframework.web.HttpMediaTypeNotSupportedException;
import org.springframework.web.HttpRequestMethodNotSupportedException;
import org.springframework.web.bind.annotation.ExceptionHandler;
import org.springframework.web.bind.annotation.RestControllerAdvice;
import org.springframework.web.method.annotation.MethodArgumentTypeMismatchException;
import org.springframework.web.multipart.MaxUploadSizeExceededException;
import org.springframework.web.multipart.MultipartException;
import org.springframework.web.multipart.support.MissingServletRequestPartException;
import org.springframework.web.servlet.resource.NoResourceFoundException;

import jakarta.servlet.http.HttpServletRequest;
import java.util.Map;

@RestControllerAdvice
public class GlobalExceptionHandler {

    private static final Logger log = LoggerFactory.getLogger(GlobalExceptionHandler.class);

    @ExceptionHandler(ApiException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleApi(ApiException e, HttpServletRequest request) {
        return build(e.status(), e.code(), e.getMessage(), e.details(), request);
    }

    @ExceptionHandler(HttpMessageNotReadableException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleBadJson(HttpServletRequest request) {
        return build(HttpStatus.BAD_REQUEST, "INVALID_JSON", "Некорректный JSON в запросе", null, request);
    }

    @ExceptionHandler(MissingServletRequestPartException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleMissingPart(HttpServletRequest request) {
        return build(HttpStatus.UNPROCESSABLE_ENTITY, "VALIDATION_ERROR",
                "Выберите файл аватарки", Map.of("avatar", "файл обязателен"), request);
    }

    @ExceptionHandler(MaxUploadSizeExceededException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleMaxUpload(HttpServletRequest request) {
        return build(HttpStatus.PAYLOAD_TOO_LARGE, "AVATAR_TOO_LARGE", "Файл слишком большой", null, request);
    }

    @ExceptionHandler(MultipartException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleMultipart(HttpServletRequest request) {
        return build(HttpStatus.BAD_REQUEST, "INVALID_MULTIPART", "Некорректный multipart-запрос", null, request);
    }

    @ExceptionHandler(HttpRequestMethodNotSupportedException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleMethod(HttpServletRequest request) {
        return build(HttpStatus.METHOD_NOT_ALLOWED, "METHOD_NOT_ALLOWED", "Метод не поддерживается", null, request);
    }

    @ExceptionHandler(HttpMediaTypeNotSupportedException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleMediaType(HttpServletRequest request) {
        return build(HttpStatus.UNSUPPORTED_MEDIA_TYPE, "UNSUPPORTED_MEDIA_TYPE",
                "Неподдерживаемый тип содержимого", null, request);
    }

    @ExceptionHandler(MethodArgumentTypeMismatchException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleTypeMismatch(
            MethodArgumentTypeMismatchException e, HttpServletRequest request) {
        return build(HttpStatus.BAD_REQUEST, "VALIDATION_ERROR", "Некорректный параметр запроса",
                Map.of(e.getName(), "некорректное значение"), request);
    }

    @ExceptionHandler(NoResourceFoundException.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleNoResource(HttpServletRequest request) {
        return build(HttpStatus.NOT_FOUND, "NOT_FOUND", "Маршрут не найден", null, request);
    }

    @ExceptionHandler(Exception.class)
    public ResponseEntity<ErrorDtos.ErrorEnvelope> handleUnexpected(Exception e, HttpServletRequest request) {
        log.error("request failed method={} path={} requestId={}",
                request.getMethod(), request.getRequestURI(), RequestContext.requestId(request), e);
        return build(HttpStatus.INTERNAL_SERVER_ERROR, "INTERNAL", "Внутренняя ошибка сервера", null, request);
    }

    private ResponseEntity<ErrorDtos.ErrorEnvelope> build(HttpStatus status,
                                                          String code,
                                                          String message,
                                                          Object details,
                                                          HttpServletRequest request) {
        ErrorDtos.ErrorBody body = new ErrorDtos.ErrorBody(
                code, message, details, RequestContext.requestId(request));
        return ResponseEntity.status(status).body(new ErrorDtos.ErrorEnvelope(body));
    }
}
