using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RegistrAi.Api.Data;

namespace RegistrAi.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DashboardController : ControllerBase
    {
        private readonly AppDbContext _context;

        public DashboardController(AppDbContext context)
        {
            _context = context;
        }
         [HttpGet]
        public async Task<IActionResult> ObterDashboard([FromQuery] string periodo = "mes")
        {
            // 1. CALCULA AS DATAS DO PERÍODO AUTOMATICAMENTE
            var hoje = DateTime.Today;
            DateTime inicio;
            DateTime fim = hoje;

            if (periodo == "semana")
            {
                // Pega o início da semana atual (segunda-feira)
                var diasDesdeSegunda = ((int)hoje.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
                inicio = hoje.AddDays(-diasDesdeSegunda);
            }
            else if (periodo == "ano") {
                inicio = new DateTime(hoje.Year, 1, 1);
            }
            else
            {
                // Pega o início e fim do mês atual
                inicio = new DateTime(hoje.Year, hoje.Month, 1);
            }

            // Garante que o fim do período inclui o dia inteiro (23:59:59)
            var fimDoDia = fim.Date.AddDays(1).AddTicks(-1);

            // 2. BUSCA TODAS AS TRANSAÇÕES DO PERÍODO (uma única vez no banco)
            var transacoes = await _context.Transacoes
                .Where(t => t.Data >= inicio && t.Data <= fimDoDia)
                .ToListAsync();

            // 3. BUSCA SEPARADA PARA VARIAÇÃO 24H
            var ontem = hoje.AddDays(-1);
            var transacoesOntem = await _context.Transacoes
                .Where(t => t.Data.Date == ontem)
                .ToListAsync();
            
            var transacoesHoje = transacoes
                .Where(t => t.Data.Date == hoje)
                .ToList();

            // 4. TOTAIS GERAIS (números do topo do dashboard)
            var totalReceitas = transacoes
                .Where(t => t.Tipo == "Receita")
                .Sum(t => t.Valor);

            var totalDespesas = transacoes
                .Where(t => t.Tipo == "Despesa")
                .Sum(t => t.Valor);

            var saldo = totalReceitas - totalDespesas;

            var quantidadeTransacoes = transacoes.Count;

            // 5. VARIAÇÃO 24H
            var despesasHoje = transacoesHoje
                .Where(t => t.Tipo == "Despesa")
                .Sum(t => t.Valor);

            var despesasOntem = transacoesOntem
                .Where(t => t.Tipo == "Despesa")
                .Sum(t => t.Valor);

            var variacao24h = despesasHoje - despesasOntem;

            // 6. EVOLUÇÃO POR DIA (dados para o gráfico de linha/barras)
            // Gera todos os dias do período, mesmo os que não têm transação
            var todosDias = Enumerable
                .Range(0, (fim - inicio).Days + 1)
                .Select(d => inicio.AddDays(d).Date)
                .ToList();

            var porDia = todosDias.Select(dia => new
            {
                Dia = dia.ToString("yyyy-MM-dd"),
                Receitas = transacoes
                    .Where(t => t.Data.Date == dia && t.Tipo == "Receita")
                    .Sum(t => t.Valor),
                Despesas = transacoes
                    .Where(t => t.Data.Date == dia && t.Tipo == "Despesa")
                    .Sum(t => t.Valor)
            }).ToList();

            // 7. GASTOS POR CATEGORIA (dados para o gráfico de pizza)
           var porCategoria = transacoes
            .GroupBy(t => new { t.Categoria, t.Tipo })
            .Select(g => new
            {
                Categoria = g.Key.Categoria,
                Tipo = g.Key.Tipo,
                Total = g.Sum(t => t.Valor),
                Percentual = g.Key.Tipo == "Despesa"
                    ? (totalDespesas > 0
                        ? Math.Round((g.Sum(t => t.Valor) / totalDespesas) * 100, 1)
                        : 0)
                    : (totalReceitas > 0
                        ? Math.Round((g.Sum(t => t.Valor) / totalReceitas) * 100, 1)
                        : 0)
            })
            .OrderByDescending(c => c.Total)
            .ToList();

            var ultimasTransacoes = transacoes
            .OrderByDescending(t => t.Data)
            .Take(5)
            .Select(t => new
            {
                t.Id,
                t.Valor,
                t.Categoria,
                t.Data,
                t.Tipo
            })
            .ToList();

                // 8. MONTA E RETORNA O OBJETO COMPLETO
            return Ok(new
            {
                Periodo = periodo,
                DataInicio = inicio.ToString("yyyy-MM-dd"),
                DataFim = fim.ToString("yyyy-MM-dd"),
                TotalReceitas = totalReceitas,
                TotalDespesas = totalDespesas,
                Saldo = saldo,
                QuantidadeTransacoes = quantidadeTransacoes,
                Variacao24h = variacao24h,
                PorDia = porDia,
                PorCategoria = porCategoria,
                UltimasTransacoes = ultimasTransacoes,
            });
        }
    }
}