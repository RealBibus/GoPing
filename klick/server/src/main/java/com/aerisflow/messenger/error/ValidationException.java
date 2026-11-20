package com.aerisflow.messenger.error;

import org.springframework.http.HttpStatus;

import java.util.Map;

/**
 * Ошибка валидации: 422 + карта поле -> сообщение.
 */
public class ValidationException extends ApiException {

    public ValidationException(Map<String, String> fields) {
        super(HttpStatus.UNPROCESSABLE_ENTITY, "VALIDATION_ERROR",
                "Проверьте правильность заполнения полей", fields);
    }
}
