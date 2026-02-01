using System.Data;
using VilarDriverApi.Models;
using VilarDriverApi.Services;

namespace VilarDriverApi.Data
{
    public static class DbSeeder
    {
        public static void Seed(AppDbContext db)
        {
            // 1) USERS

            User EnsureUser(string login, string password, UserRole role)
            {
                var u = db.Users.FirstOrDefault(x => x.Login == login);
                if (u == null)
                {
                    u = new User
                    {
                        Login = login,
                        PasswordHash = AuthService.HashPassword(password),
                        Role = role
                    };
                    db.Users.Add(u);
                    db.SaveChanges();
                }
                return u;
            }

            // ✅ Admin zawsze ma istnieć
            var adminUser = EnsureUser("admin", "Admin123!", UserRole.Admin);

            // =========================================================
            // ✅ USUŃ "SZTYWNYCH" DRIVERÓW Z SEEDA (driver1/2/3)
            // oraz ich rekordy Driver/Vehicle, żeby w bazie zostali tylko
            // ci dodani przez "Dodaj kierowcę".
            // =========================================================

            var seedDriverLogins = new[] { "driver1", "driver2", "driver3" };

            // znajdź użytkowników seedowych driverów
            var seedUsers = db.Users
                .Where(u => seedDriverLogins.Contains(u.Login))
                .ToList();

            if (seedUsers.Count > 0)
            {
                var seedUserIds = seedUsers.Select(u => u.Id).ToList();

                // kierowcy powiązani z tymi userami
                var seedDrivers = db.Drivers
                    .Where(d => seedUserIds.Contains(d.UserId))
                    .ToList();

                var seedDriverIds = seedDrivers.Select(d => d.Id).ToList();

                // 1) odpinamy zlecenia przypisane do tych kierowców (żeby nie było FK problemów)
                if (seedDriverIds.Count > 0)
                {
                    var ordersToUnassign = db.Orders
                        .Where(o => o.DriverId.HasValue && seedDriverIds.Contains(o.DriverId.Value))
                        .ToList();

                    foreach (var o in ordersToUnassign)
                        o.DriverId = null;

                    db.SaveChanges();
                }

                // 2) usuń pojazdy
                if (seedDriverIds.Count > 0)
                {
                    var vehicles = db.Vehicles
                        .Where(v => seedDriverIds.Contains(v.DriverId))
                        .ToList();

                    if (vehicles.Count > 0)
                    {
                        db.Vehicles.RemoveRange(vehicles);
                        db.SaveChanges();
                    }
                }

                // 3) usuń driverów
                if (seedDrivers.Count > 0)
                {
                    db.Drivers.RemoveRange(seedDrivers);
                    db.SaveChanges();
                }

                // 4) usuń userów driver1/2/3
                db.Users.RemoveRange(seedUsers);
                db.SaveChanges();
            }

            // =========================================================
            // 4) ORDERS (demo)
            // ✅ Nie przypisujemy do driverów z seeda (bo już ich nie ma).
            // Zostawiamy DriverId = null, a admin może przypisać w panelu.
            // =========================================================
            var today = DateTime.Today;

            if (!db.Orders.Any(o => o.OrderNumber == "ZL-0001"))
            {
                db.Orders.Add(new Order
                {
                    OrderNumber = "ZL-0001",
                    PickupAddress = "Warszawa, ul. Przykładowa 1",
                    DeliveryAddress = "Poznań, ul. Testowa 10",
                    PickupTime = today.AddHours(9),
                    DeliveryTime = today.AddHours(15),
                    CargoInfo = "10 palet, 5200kg, uwagi: ostrożnie",
                    DriverId = null,
                    Status = OrderStatus.ToPickup
                });
            }

            if (!db.Orders.Any(o => o.OrderNumber == "ZL-0002"))
            {
                db.Orders.Add(new Order
                {
                    OrderNumber = "ZL-0002",
                    PickupAddress = "Warszawa, ul. Magazynowa 5",
                    DeliveryAddress = "Łódź, ul. Transportowa 3",
                    PickupTime = today.AddHours(10),
                    DeliveryTime = today.AddHours(14),
                    CargoInfo = "4 palety, 1200kg",
                    DriverId = null,
                    Status = OrderStatus.ToPickup
                });
            }

            if (!db.Orders.Any(o => o.OrderNumber == "ZL-0003"))
            {
                db.Orders.Add(new Order
                {
                    OrderNumber = "ZL-0003",
                    PickupAddress = "Warszawa, ul. Logistyczna 12",
                    DeliveryAddress = "Gdańsk, ul. Portowa 8",
                    PickupTime = today.AddHours(8),
                    DeliveryTime = today.AddHours(18),
                    CargoInfo = "1 paleta, 300kg, uwagi: szkło",
                    DriverId = null,
                    Status = OrderStatus.ToPickup
                });
            }

            if (!db.Orders.Any(o => o.OrderNumber == "ZL-0004"))
            {
                db.Orders.Add(new Order
                {
                    OrderNumber = "ZL-0004",
                    PickupAddress = "Warszawa, ul. Jakaś 4",
                    DeliveryAddress = "Gdańsk, ul. Kolejowa 4",
                    PickupTime = DateTime.Today.AddDays(5).AddHours(8),
                    DeliveryTime = DateTime.Today.AddDays(6).AddHours(8),
                    CargoInfo = "1 paleta, 300kg, uwagi: szkło",
                    DriverId = null,
                    Status = OrderStatus.ToPickup
                });
            }

            db.SaveChanges();
        }
    }
}
