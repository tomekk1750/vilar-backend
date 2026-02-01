using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using VilarDriverApi.Models;

namespace VilarDriverApi.Controllers
{
    [ApiController]
    [Route("api/meta")]
    public class MetaController : ControllerBase
    {
        [HttpGet("order-statuses")]
        public IActionResult GetOrderStatuses()
        {
            var values = Enum.GetValues<OrderStatus>()
                .Select(v => new
                {
                    value = (int)v,
                    label = v.ToString()
                })
                .ToList();

            return Ok(values);
        }
    }
}
