package com.goping.config;

import com.goping.db.SqliteExceptionTranslator;
import com.zaxxer.hikari.HikariConfig;
import com.zaxxer.hikari.HikariDataSource;
import org.sqlite.SQLiteConfig;
import org.sqlite.SQLiteDataSource;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.jdbc.core.JdbcTemplate;

import javax.sql.DataSource;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

/**
 * SQLite: WAL, foreign keys, busy timeout.
 * Пул нужен для параллельных чтений; записи SQLite всё равно сериализует.
 */
@Configuration
public class DataSourceConfig {

    @Bean
    public DataSource dataSource(AppProperties properties) throws IOException {
        Path databasePath = Paths.get(properties.databasePath()).toAbsolutePath().normalize();
        Path parent = databasePath.getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }

        SQLiteConfig sqliteConfig = new SQLiteConfig();
        sqliteConfig.enforceForeignKeys(true);
        sqliteConfig.setJournalMode(SQLiteConfig.JournalMode.WAL);
        sqliteConfig.setSynchronous(SQLiteConfig.SynchronousMode.NORMAL);
        sqliteConfig.setBusyTimeout(5000);

        SQLiteDataSource sqliteDataSource = new SQLiteDataSource(sqliteConfig);
        sqliteDataSource.setUrl("jdbc:sqlite:" + databasePath);

        HikariConfig hikariConfig = new HikariConfig();
        hikariConfig.setPoolName("sqlite-pool");
        hikariConfig.setDataSource(sqliteDataSource);
        hikariConfig.setMaximumPoolSize(5);
        hikariConfig.setMinimumIdle(1);
        hikariConfig.setConnectionTimeout(10_000);
        hikariConfig.setMaxLifetime(30 * 60_000L);

        return new HikariDataSource(hikariConfig);
    }

    /**
     * Свой JdbcTemplate нужен ради SQLite-трансляции ошибок: иначе
     * UNIQUE-конфликты превращаются в 500 вместо 409.
     */
    @Bean
    public JdbcTemplate jdbcTemplate(DataSource dataSource) {
        JdbcTemplate jdbcTemplate = new JdbcTemplate(dataSource);
        jdbcTemplate.setExceptionTranslator(new SqliteExceptionTranslator());
        return jdbcTemplate;
    }
}
