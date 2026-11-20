package com.goping.web;

import com.goping.error.ApiException;
import org.springframework.http.HttpStatus;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

import javax.sql.DataSource;
import java.sql.Connection;
import java.sql.SQLException;
import java.util.Map;

@RestController
public class HealthController {

    private final DataSource dataSource;

    public HealthController(DataSource dataSource) {
        this.dataSource = dataSource;
    }

    @GetMapping("/healthz")
    public Map<String, String> health() {
        return Map.of("status", "ok");
    }

    @GetMapping("/readyz")
    public Map<String, String> ready() {
        try (Connection connection = dataSource.getConnection()) {
            if (!connection.isValid(2)) {
                throw new SQLException("connection is not valid");
            }
        } catch (SQLException e) {
            throw new ApiException(HttpStatus.SERVICE_UNAVAILABLE, "DB_UNAVAILABLE",
                    "База данных недоступна");
        }
        return Map.of("status", "ready");
    }
}
