using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RegistrAi.Api.Data;

namespace RegistrAi.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ResumoController : ControllerBase
    {
        private readonly AppDbContext _context;

        public ResumoController(AppDbContext context)
        {
            _context = context;
        }

        // Endpoint: GET /api/resumo?inicio=2024-01-01&fim=2024-01-31
        [HttpGet]
        public async Task<IActionResult> ObterResumo([FromQuery] DateTime? inicio, [FromQuery] DateTime? fim)
        {
            // Criamos a consulta base (ainda sem executar no banco)
            var consulta = _context.Transacoes.AsQueryable();

            // 1. Aplicamos o filtro de data de início, se o utilizador enviou
            if (inicio.HasValue)
            {
                consulta = consulta.Where(t => t.Data >= inicio.Value);
            }

            // 2. Aplicamos o filtro de data de fim, se o utilizador enviou
            if (fim.HasValue)
            {
                // Ajustamos para o final do dia (23:59:59) para não perder transações do último dia
                var dataFimComHora = fim.Value.Date.AddDays(1).AddTicks(-1);
                consulta = consulta.Where(t => t.Data <= dataFimComHora);
            }

            // 3. Agora sim, mandamos o banco somar apenas o que restou nos filtros
            var totalReceitas = await consulta
                .Where(t => t.Tipo == "Receita")
                .SumAsync(t => t.Valor);

            var totalDespesas = await consulta
                .Where(t => t.Tipo == "Despesa")
                .SumAsync(t => t.Valor);

            var saldo = totalReceitas - totalDespesas;

            return Ok(new
            {
                DataInicio = inicio,
                DataFim = fim,
                TotalReceitas = totalReceitas,
                TotalDespesas = totalDespesas,
                Saldo = saldo,
                QuantidadeTransacoes = await consulta.CountAsync() // Um extra: contar quantas transações houve no período
            });
        }
    }
}