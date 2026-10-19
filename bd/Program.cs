using Npgsql;
using NpgsqlTypes;
using System.Globalization;

internal static class Program
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CINEMA_CONNECTION_STRING")
        ?? "Host=localhost;Port=5432;Database=cinema_training;Username=postgres;Password=YOUR_PASSWORD";

    private static async Task Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== Система продажи билетов в кинотеатр ===");
        try
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT CURRENT_TIMESTAMP", connection);
            var serverTime = await command.ExecuteScalarAsync();
            Console.WriteLine($"Подключение к PostgreSQL успешно. Время сервера: {serverTime}\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Не удалось подключиться к базе данных.");
            Console.WriteLine($"Подробности: {ex.Message}");
            Console.WriteLine("Проверьте PostgreSQL, базу cinema_training и переменную CINEMA_CONNECTION_STRING.");
            return;
        }

        while (true)
        {
            Console.WriteLine("\nМЕНЮ");
            Console.WriteLine("1. Список фильмов и поиск");
            Console.WriteLine("2. Расписание и свободные места");
            Console.WriteLine("3. Найти клиента и его билеты");
            Console.WriteLine("4. Зарегистрировать клиента");
            Console.WriteLine("5. Купить билет");
            Console.WriteLine("6. Отменить билет");
            Console.WriteLine("7. Отчёт по сеансам и топ-3 фильма");
            Console.WriteLine("0. Выход");
            Console.Write("Выберите действие: ");
            string? choice = Console.ReadLine()?.Trim();

            try
            {
                switch (choice)
                {
                    case "1": await ShowFilmsAsync(); break;
                    case "2": await ShowScheduleAsync(); break;
                    case "3": await FindCustomerTicketsAsync(); break;
                    case "4": await RegisterCustomerAsync(); break;
                    case "5": await BuyTicketAsync(); break;
                    case "6": await CancelTicketAsync(); break;
                    case "7": await ShowReportsAsync(); break;
                    case "0": Console.WriteLine("До свидания!"); return;
                    default: Console.WriteLine("Неизвестный пункт меню."); break;
                }
            }
            catch (PostgresException ex)
            {
                Console.WriteLine($"Ошибка базы данных: {ex.MessageText}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
        }
    }

    private static NpgsqlConnection CreateConnection() => new(ConnectionString);

    private static async Task ShowFilmsAsync()
    {
        Console.Write("Введите часть названия (Enter — все фильмы): ");
        string search = Console.ReadLine()?.Trim() ?? "";
        const string sql = """
            SELECT film_id, title, genre, duration_minutes, age_rating, release_year
            FROM films
            WHERE (@search = '' OR title ILIKE '%' || @search || '%')
            ORDER BY title;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("search", search);
        await using var reader = await command.ExecuteReaderAsync();
        Console.WriteLine("\nID | Название | Жанр | Мин. | Возраст | Год");
        bool found = false;
        while (await reader.ReadAsync())
        {
            found = true;
            Console.WriteLine($"{reader.GetInt32(0)} | {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetInt32(3)} | {reader.GetInt32(4)}+ | {reader.GetInt32(5)}");
        }
        if (!found) Console.WriteLine("Фильмы не найдены.");
    }

    private static async Task ShowScheduleAsync()
    {
        Console.WriteLine("Фильтр: 1 — все даты, 2 — конкретная дата, 3 — диапазон дат");
        string mode = ReadRequired("Ваш выбор: ");
        DateOnly? from = null, to = null;
        if (mode == "2") from = to = ReadDate("Дата (ГГГГ-ММ-ДД): ");
        else if (mode == "3")
        {
            from = ReadDate("Начальная дата (ГГГГ-ММ-ДД): ");
            to = ReadDate("Конечная дата (ГГГГ-ММ-ДД): ");
            if (to < from) { Console.WriteLine("Конечная дата раньше начальной."); return; }
        }
        else if (mode != "1") { Console.WriteLine("Некорректный режим."); return; }

        const string sql = """
            SELECT sc.screening_id, f.title, f.genre, sc.starts_at, h.hall_name,
                   sc.ticket_price, h.seat_count,
                   h.seat_count - COUNT(t.ticket_id) FILTER (WHERE t.status = 'Purchased') AS free_seats
            FROM screenings sc
            JOIN films f ON f.film_id = sc.film_id
            JOIN halls h ON h.hall_id = sc.hall_id
            LEFT JOIN tickets t ON t.screening_id = sc.screening_id
            WHERE (@from_date IS NULL OR sc.starts_at::date >= @from_date)
              AND (@to_date IS NULL OR sc.starts_at::date <= @to_date)
            GROUP BY sc.screening_id, f.title, f.genre, sc.starts_at, h.hall_name,
                     sc.ticket_price, h.seat_count
            ORDER BY sc.starts_at;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("from_date", NpgsqlDbType.Date).Value = from.HasValue ? from.Value : DBNull.Value;
        command.Parameters.Add("to_date", NpgsqlDbType.Date).Value = to.HasValue ? to.Value : DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync();
        Console.WriteLine("\nID | Фильм | Жанр | Начало | Зал | Цена | Всего мест | Свободно");
        bool found = false;
        while (await reader.ReadAsync())
        {
            found = true;
            Console.WriteLine($"{reader.GetInt32(0)} | {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetDateTime(3):dd.MM.yyyy HH:mm} | {reader.GetString(4)} | {reader.GetDecimal(5):0.00} | {reader.GetInt32(6)} | {reader.GetInt64(7)}");
        }
        if (!found) Console.WriteLine("Сеансов за выбранный период нет.");
    }

    private static async Task FindCustomerTicketsAsync()
    {
        string email = ReadRequired("Email клиента: ");
        const string sql = """
            SELECT c.customer_id, c.first_name, c.last_name, t.ticket_id, f.title,
                   sc.starts_at, h.hall_name, st.seat_number, sc.ticket_price, t.status
            FROM customers c
            LEFT JOIN tickets t ON t.customer_id = c.customer_id
            LEFT JOIN screenings sc ON sc.screening_id = t.screening_id
            LEFT JOIN films f ON f.film_id = sc.film_id
            LEFT JOIN halls h ON h.hall_id = t.hall_id
            LEFT JOIN seats st ON st.seat_id = t.seat_id
            WHERE LOWER(c.email) = LOWER(@email)
            ORDER BY sc.starts_at NULLS LAST, t.ticket_id;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("email", email);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) { Console.WriteLine("Клиент с таким email не найден."); return; }
        Console.WriteLine($"Клиент: {reader.GetString(1)} {reader.GetString(2)} (ID {reader.GetInt32(0)})");
        do
        {
            if (reader.IsDBNull(3)) { Console.WriteLine("У клиента пока нет билетов."); continue; }
            Console.WriteLine($"Билет #{reader.GetInt32(3)} | {reader.GetString(4)} | {reader.GetDateTime(5):dd.MM.yyyy HH:mm} | {reader.GetString(6)} | место {reader.GetInt32(7)} | {reader.GetDecimal(8):0.00} | {reader.GetString(9)}");
        } while (await reader.ReadAsync());
    }

    private static async Task RegisterCustomerAsync()
    {
        string firstName = ReadRequired("Имя: ");
        string lastName = ReadRequired("Фамилия: ");
        string email = ReadRequired("Email: ");
        string phone = ReadOptional("Телефон (можно пропустить): ");
        const string sql = """
            INSERT INTO customers(first_name, last_name, email, phone)
            VALUES (@first_name, @last_name, @email, @phone)
            RETURNING customer_id;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("first_name", firstName);
        command.Parameters.AddWithValue("last_name", lastName);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.Add("phone", NpgsqlDbType.Varchar).Value = string.IsNullOrWhiteSpace(phone) ? DBNull.Value : phone;
        try
        {
            int id = Convert.ToInt32(await command.ExecuteScalarAsync());
            Console.WriteLine($"Клиент успешно зарегистрирован. ID: {id}");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            Console.WriteLine("Клиент с таким email уже зарегистрирован.");
        }
    }

    private static async Task BuyTicketAsync()
    {
        int screeningId = ReadInt("ID сеанса: ");
        int customerId = ReadInt("ID клиента: ");
        int seatNumber = ReadInt("Номер места: ");
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {

            const string screeningSql = """
                SELECT sc.hall_id, f.title, sc.starts_at, sc.ticket_price
                FROM screenings sc JOIN films f ON f.film_id = sc.film_id
                WHERE sc.screening_id = @id FOR UPDATE OF sc;
                """;
            int hallId; string title; DateTime startsAt; decimal price;
            await using (var command = new NpgsqlCommand(screeningSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", screeningId);
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) { Console.WriteLine("Сеанс не найден."); await transaction.RollbackAsync(); return; }
                hallId = reader.GetInt32(0); title = reader.GetString(1); startsAt = reader.GetDateTime(2); price = reader.GetDecimal(3);
            }
            await using (var customerCommand = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM customers WHERE customer_id = @id)", connection, transaction))
            {
                customerCommand.Parameters.AddWithValue("id", customerId);
                if (!(bool)(await customerCommand.ExecuteScalarAsync() ?? false))
                { Console.WriteLine("Клиент не найден."); await transaction.RollbackAsync(); return; }
            }
            int seatId;
            await using (var seatCommand = new NpgsqlCommand("SELECT seat_id FROM seats WHERE hall_id = @hall AND seat_number = @number", connection, transaction))
            {
                seatCommand.Parameters.AddWithValue("hall", hallId);
                seatCommand.Parameters.AddWithValue("number", seatNumber);
                object? result = await seatCommand.ExecuteScalarAsync();
                if (result is null) { Console.WriteLine("Такого места в зале выбранного сеанса нет."); await transaction.RollbackAsync(); return; }
                seatId = Convert.ToInt32(result);
            }
            await using (var availability = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM tickets WHERE screening_id = @screening AND seat_id = @seat AND status = 'Purchased')", connection, transaction))
            {
                availability.Parameters.AddWithValue("screening", screeningId);
                availability.Parameters.AddWithValue("seat", seatId);
                if ((bool)(await availability.ExecuteScalarAsync() ?? false))
                { Console.WriteLine("Это место уже занято."); await transaction.RollbackAsync(); return; }
            }
            int ticketId;
            await using (var insert = new NpgsqlCommand("INSERT INTO tickets(screening_id, hall_id, seat_id, customer_id, status) VALUES (@screening, @hall, @seat, @customer, 'Purchased') RETURNING ticket_id", connection, transaction))
            {
                insert.Parameters.AddWithValue("screening", screeningId);
                insert.Parameters.AddWithValue("hall", hallId);
                insert.Parameters.AddWithValue("seat", seatId);
                insert.Parameters.AddWithValue("customer", customerId);
                ticketId = Convert.ToInt32(await insert.ExecuteScalarAsync());
            }
            await transaction.CommitAsync();
            Console.WriteLine($"Покупка подтверждена! Билет #{ticketId}; {title}; {startsAt:dd.MM.yyyy HH:mm}; место {seatNumber}; цена {price:0.00}.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await transaction.RollbackAsync();
            Console.WriteLine("Покупка не выполнена: место уже занято другим покупателем.");
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task CancelTicketAsync()
    {
        int ticketId = ReadInt("Номер билета: ");
        const string sql = "UPDATE tickets SET status = 'Cancelled' WHERE ticket_id = @id AND status = 'Purchased'";
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", ticketId);
        int affected = await command.ExecuteNonQueryAsync();
        if (affected == 1) Console.WriteLine("Билет отменён. Место снова доступно для продажи.");
        else
        {
            await using var check = new NpgsqlCommand("SELECT status FROM tickets WHERE ticket_id = @id", connection);
            check.Parameters.AddWithValue("id", ticketId);
            object? status = await check.ExecuteScalarAsync();
            Console.WriteLine(status is null ? "Билет не найден." : "Билет уже отменён.");
        }
    }

    private static async Task ShowReportsAsync()
    {
        const string reportSql = """
            SELECT f.title, sc.starts_at, h.hall_name,
                   COUNT(t.ticket_id) FILTER (WHERE t.status = 'Purchased') AS sold,
                   COALESCE(SUM(sc.ticket_price) FILTER (WHERE t.status = 'Purchased'), 0)::numeric(12,2) AS revenue
            FROM screenings sc
            JOIN films f ON f.film_id = sc.film_id
            JOIN halls h ON h.hall_id = sc.hall_id
            LEFT JOIN tickets t ON t.screening_id = sc.screening_id
            GROUP BY sc.screening_id, f.title, sc.starts_at, h.hall_name
            ORDER BY sc.starts_at;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand(reportSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Console.WriteLine("\nОТЧЁТ ПО СЕАНСАМ");
            Console.WriteLine("Фильм | Дата и время | Зал | Продано | Выручка");
            while (await reader.ReadAsync())
                Console.WriteLine($"{reader.GetString(0)} | {reader.GetDateTime(1):dd.MM.yyyy HH:mm} | {reader.GetString(2)} | {reader.GetInt64(3)} | {reader.GetDecimal(4):0.00}");
        }
        const string topSql = """
            SELECT f.title, COUNT(t.ticket_id) AS sold
            FROM films f
            JOIN screenings sc ON sc.film_id = f.film_id
            JOIN tickets t ON t.screening_id = sc.screening_id AND t.status = 'Purchased'
            GROUP BY f.film_id, f.title
            ORDER BY sold DESC, f.title
            LIMIT 3;
            """;
        await using (var command = new NpgsqlCommand(topSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Console.WriteLine("\nТОП-3 ФИЛЬМА ПО ПРОДАННЫМ БИЛЕТАМ");
            int place = 1;
            while (await reader.ReadAsync()) Console.WriteLine($"{place++}. {reader.GetString(0)} — {reader.GetInt64(1)} бил.");
            if (place == 1) Console.WriteLine("Продаж пока нет.");
        }
    }

    private static string ReadRequired(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string value = Console.ReadLine()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(value)) return value;
            Console.WriteLine("Поле обязательно для заполнения.");
        }
    }

    private static string ReadOptional(string prompt) { Console.Write(prompt); return Console.ReadLine()?.Trim() ?? ""; }

    private static int ReadInt(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            if (int.TryParse(Console.ReadLine(), out int value) && value > 0) return value;
            Console.WriteLine("Введите положительное целое число.");
        }
    }

    private static DateOnly ReadDate(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            if (DateOnly.TryParseExact(Console.ReadLine(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            Console.WriteLine("Неверная дата. Формат: ГГГГ-ММ-ДД.");
        }
    }
}
