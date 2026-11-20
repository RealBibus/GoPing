package com.aerisflow.messenger.db;

import org.springframework.dao.DataAccessException;
import org.springframework.dao.DataIntegrityViolationException;
import org.springframework.dao.DuplicateKeyException;
import org.springframework.jdbc.support.SQLExceptionSubclassTranslator;
import org.springframework.jdbc.support.SQLExceptionTranslator;

import java.sql.SQLException;

/**
 * SQLite не описана в стандартном sql-error-codes Spring, поэтому уникальные
 * ограничения приходят как UncategorizedSQLException. Переводим их в обычные
 * DuplicateKeyException, чтобы сервисы могли отдать 409.
 */
public class SqliteExceptionTranslator implements SQLExceptionTranslator {

    private static final int SQLITE_CONSTRAINT = 19;

    private final SQLExceptionTranslator fallback = new SQLExceptionSubclassTranslator();

    @Override
    public DataAccessException translate(String task, String sql, SQLException exception) {
        if (exception == null) {
            return null;
        }

        String message = exception.getMessage() == null ? "" : exception.getMessage();
        boolean constraint = exception.getErrorCode() == SQLITE_CONSTRAINT
                || "23000".equals(exception.getSQLState());

        if (constraint && message.contains("UNIQUE constraint failed")) {
            return new DuplicateKeyException(buildMessage(task, sql, exception), exception);
        }
        if (constraint) {
            return new DataIntegrityViolationException(buildMessage(task, sql, exception), exception);
        }

        return fallback.translate(task, sql, exception);
    }

    private String buildMessage(String task, String sql, SQLException exception) {
        return "SQL [" + sql + "]; " + task + "; " + exception.getMessage();
    }
}
