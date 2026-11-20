package com.goping.db;

import com.goping.config.AppProperties;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.boot.ApplicationArguments;
import org.springframework.boot.ApplicationRunner;
import org.springframework.core.io.Resource;
import org.springframework.core.io.support.EncodedResource;
import org.springframework.core.io.support.PathMatchingResourcePatternResolver;
import org.springframework.jdbc.datasource.init.ScriptUtils;
import org.springframework.stereotype.Component;

import javax.sql.DataSource;
import java.nio.charset.StandardCharsets;
import java.sql.Connection;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.util.Arrays;
import java.util.Comparator;

/**
 * Простой мигратор: classpath:db/migration/*.sql по порядку имён.
 * Уже применённые версии хранятся в schema_migrations.
 */
@Component
public class DatabaseMigrator implements ApplicationRunner {

    private static final Logger log = LoggerFactory.getLogger(DatabaseMigrator.class);

    private final DataSource dataSource;
    private final AppProperties properties;

    public DatabaseMigrator(DataSource dataSource, AppProperties properties) {
        this.dataSource = dataSource;
        this.properties = properties;
    }

    @Override
    public void run(ApplicationArguments args) throws Exception {
        try (Connection connection = dataSource.getConnection()) {
            try (PreparedStatement statement = connection.prepareStatement("""
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version    TEXT PRIMARY KEY,
                        applied_at INTEGER NOT NULL
                    )
                    """)) {
                statement.execute();
            }

            if (!properties.autoMigrate()) {
                log.info("Auto-migration disabled, skipping SQL migrations");
                return;
            }

            Resource[] resources = new PathMatchingResourcePatternResolver()
                    .getResources("classpath:db/migration/*.sql");
            Arrays.sort(resources, Comparator.comparing(Resource::getFilename));

            for (Resource resource : resources) {
                String version = resource.getFilename();
                if (version == null || isApplied(connection, version)) {
                    continue;
                }
                apply(connection, resource, version);
                log.info("Applied migration {}", version);
            }
        }
    }

    private boolean isApplied(Connection connection, String version) throws Exception {
        try (PreparedStatement statement = connection.prepareStatement(
                "SELECT 1 FROM schema_migrations WHERE version = ?")) {
            statement.setString(1, version);
            try (ResultSet resultSet = statement.executeQuery()) {
                return resultSet.next();
            }
        }
    }

    private void apply(Connection connection, Resource resource, String version) throws Exception {
        boolean previousAutoCommit = connection.getAutoCommit();
        connection.setAutoCommit(false);
        try {
            ScriptUtils.executeSqlScript(connection, new EncodedResource(resource, StandardCharsets.UTF_8));
            try (PreparedStatement statement = connection.prepareStatement(
                    "INSERT INTO schema_migrations (version, applied_at) VALUES (?, ?)")) {
                statement.setString(1, version);
                statement.setLong(2, System.currentTimeMillis());
                statement.executeUpdate();
            }
            connection.commit();
        } catch (Exception e) {
            connection.rollback();
            throw new IllegalStateException("Failed to apply migration " + version, e);
        } finally {
            connection.setAutoCommit(previousAutoCommit);
        }
    }
}
