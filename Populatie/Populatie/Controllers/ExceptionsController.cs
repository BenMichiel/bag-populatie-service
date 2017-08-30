// In ExceptionsController.cs bevinden zich de onderdelen van de webAPI,
// die het ophalen en aanpassen van de uitzonderingsgebieden regelen.

using Microsoft.AspNet.Identity;
using NetTopologySuite.IO;
using NetTopologySuite.Simplify;
using Populator.Models;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Spatial;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Hosting;
using System.Web.Http;

/// <summary>
/// Binnen de namespace Populator.Controllers zijn alle webAPI controllers gedefinieerd.
/// </summary>
namespace Populator.Controllers
{
	/// <summary>
	/// ExceptionsController is de controller van de webAPI voor de uitzonderingsgebieden.
	/// </summary>
	[Authorize, RoutePrefix("api")]
	public class ExceptionsController : ApiController
	{
		private ApplicationDbContext db = new ApplicationDbContext();

		/// <summary>
		/// Haal de uitzonderingsgebieden van een rekengeval op.
		/// </summary>
		/// <param name="modelId">Id van het model.</param>
		/// <param name="projectId">Id van het project.</param>
		/// <param name="caseId">Id van het rekengeval.</param>
		/// <returns>Uitzonderingsgebieden</returns>
		[Route("{modelId}/Projects/{projectId}/Cases/{caseId}/Exceptions")]
		public async Task<IHttpActionResult> GetAll(string modelId, int projectId, int caseId)
		{
			return Ok((await db.Exceptions
			 .Where(e => e.CaseId == caseId)
			 .ToArrayAsync())
			 .Select(e =>
			 {
				 //e.geo = e.geo.Buffer(0);
				 e.bbox = e.geo.Envelope;
				 e.area = (e.geo.Area.HasValue) ? e.geo.Area.Value : 0.0;
				 return e;
			 }));
		}

		/// <summary>
		/// Haal de uitzonderingsgebieden van een rekengeval op binnen een bepaalde kaartuitsnede.
		/// </summary>
		/// <param name="modelId">Id van het model.</param>
		/// <param name="projectId">Id van het project.</param>
		/// <param name="caseId">Id van het rekengeval.</param>
		/// <param name="zoom">Het zoomniveau.</param>
		/// <param name="bbox">De kaartuitsnede.</param>
		/// <returns>Uitzonderingsgebieden</returns>
		[Route("{modelId}/Projects/{projectId}/Cases/{caseId}/Exceptions")]
		public async Task<IHttpActionResult> GetAll(string modelId, int projectId, int caseId, int zoom, string bbox)
		{
			var tolerance = res[zoom] * 2;

			var bboxParts = bbox.Split(',')
			 .Take(4)
			 .Select(v =>
			 {
				 double d = double.NaN;
				 double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out d);
				 return d;
			 })
			 .ToArray();

			if (bboxParts.Count(v => !double.IsNaN(v)) != 4 || bboxParts[0] >= bboxParts[2] || bboxParts[1] >= bboxParts[3])
				return BadRequest("bounding box is invalid");

			var bboxPartsAsString = bboxParts.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray();

			var bboxGeometry = DbGeometry.FromText(System.String.Format("POLYGON (({0} {1}, {2} {1}, {2} {3}, {0} {3}, {0} {1}))", bboxPartsAsString));

			var selections = (await db.Exceptions
				.Where(e => e.CaseId == caseId)
				.ToArrayAsync())
				.Select(e =>
				{
					e.area = (e.geo.Area.HasValue) ? e.geo.Area.Value : 0.0;
					e.bbox = e.geo.Envelope;
					e.geo = e.geo.Intersects(bboxGeometry)
						? zoom > 8
							? e.geo
							: DbGeometry.FromBinary(TopologyPreservingSimplifier.Simplify(new WKTReader().Read(e.geo.AsText()), tolerance).AsBinary())
					 : e.geo.Envelope;
					return e;
				});

			return Ok(selections);
		}

		/// <summary>
		/// Haal een specifiek uitzonderingsgebied op.
		/// </summary>
		/// <param name="projectId">Id van het project.</param>
		/// <param name="caseId">Id van het rekengeval.</param>
		/// <param name="id">Id van het uitzonderingsgebied.</param>
		/// <returns>Uitzonderingsgebied.</returns>
		[Route("{modelId}/Projects/{projectId}/Cases/{caseId}/Exceptions/{id}")]
		public async Task<IHttpActionResult> Get(int projectId, int caseId, int id)
		{
			var area = await db
				.Exceptions
				.SingleAsync(e => e.Id == id && e.CaseId == caseId);

			area.area = (area.geo.Area.HasValue) ? area.geo.Area.Value : 0.0;
			area.bbox = area.geo.Envelope;

			return Ok(area);
		}

		/// <summary>
		/// Pas een uitzonderingsgebied aan.
		/// </summary>
		/// <param name="projectId">Id van het project.</param>
		/// <param name="caseId">Id van het rekengeval.</param>
		/// <param name="id">Id van het uitzonderingsgebied.</param>
		/// <param name="exception">Het uitzonderingsgebied.</param>
		/// <returns></returns>
		[Route("{modelId}/Projects/{projectId}/Cases/{caseId}/Exceptions/{id}")]
		public async Task<IHttpActionResult> Put(int projectId, int caseId, int id, Exception exception)
		{
			if (!ModelState.IsValid)
				return BadRequest(ModelState);

			using (var transaction = db.Database.BeginTransaction())
			{
				try
				{
					var exceptionInDb = db
						.Exceptions
						.Include(c => c.Case).Include(c => c.Case.Project)
						.Single(e =>
							e.Id == id &&
							e.Case.Id == caseId &&
							e.Case.Project.Id == projectId);

					if (exceptionInDb.Case.Project.UserId != User.Identity.GetUserId())
						return StatusCode(HttpStatusCode.Forbidden);

					var entry = db.Entry(exceptionInDb);

					entry.CurrentValues.SetValues(exception);

					if (entry.Property(p => p.CaseId).IsModified ||
							entry.Property(p => p.Id).IsModified)
						throw new System.Exception();

					if (entry.Property(p => p.geo).IsModified)
					{
						if (exception.geo == null || !exception.geo.IsValid)
							throw new System.Exception("Invalid geometry!");

						Helpers.RemoveCaseOutputAndResults(db, caseId, false);
					}

					exceptionInDb.Case.LastModified = System.DateTime.UtcNow;

					await db.SaveChangesAsync();

					transaction.Commit();

					return StatusCode(HttpStatusCode.NoContent);
				}
				catch (System.Exception ex)
				{
					transaction.Rollback();
					throw ex;
				}
			}
		}

		/// <summary>
		/// Voeg een nieuw uitzonderingsgebied toe.
		/// </summary>
		/// <param name="projectId">Id van het project.</param>
		/// <param name="caseId">Id van het rekengeval.</param>
		/// <param name="exception">Nieuw uitzonderingsgebied.</param>
		/// <returns></returns>
		[Route("{modelId}/Projects/{projectId}/Cases/{caseId}/Exceptions")]
		public async Task<IHttpActionResult> Post(int projectId, int caseId, Exception exception)
		{
			if (!ModelState.IsValid)
				return BadRequest(ModelState);

			using (var transaction = db.Database.BeginTransaction())
			{
				try
				{
					var caseInDb = await db
						.Cases
						.Include(c => c.Project)
						.SingleAsync(c => c.Id == caseId && c.Project.Id == projectId);

					if (caseInDb.Project.UserId != User.Identity.GetUserId())
						return StatusCode(HttpStatusCode.Forbidden);

					if (exception.geo == null || !exception.geo.IsValid)
						return BadRequest("Invalid geometry!");

					Helpers.RemoveCaseOutputAndResults(db, caseId, false);

					db.Exceptions.Add(exception);

					caseInDb.LastModified = System.DateTime.UtcNow;

					await db.SaveChangesAsync();

					transaction.Commit();

					System.Uri result = null;
					System.Uri.TryCreate(Request.RequestUri, exception.Id.ToString(), out result);

					return Created(result, exception);
				}
				catch (System.Exception ex)
				{
					transaction.Rollback();
					throw ex;
				}
			}
		}

		/// <summary>
		/// Voeg uitzonderingsgebieden toe middels shape files.
		/// </summary>
		/// <param name="modelId">Id van het model.</param>
		/// <param name="projectId">Id van het project.</param>
		/// <param name="caseId">Id van het rekengeval.</param>
		/// <returns></returns>
		[Route("{modelId}/Projects/{projectId}/Cases/{caseId}/Exceptions/Shape")]
		public async Task<IHttpActionResult> PostShape(string modelId, int projectId, int caseId)
		{
			// Check if the request contains multipart/form-data.
			if (!Request.Content.IsMimeMultipartContent())
				return StatusCode(HttpStatusCode.UnsupportedMediaType);

			using (var transaction = db.Database.BeginTransaction())
			{
				try
				{
					var caseInDb = await db
						.Cases
						.Include(c => c.Project)
						.SingleAsync(p =>
							p.Id == caseId &&
							p.Project.Id == projectId);

					if (caseInDb.Project.UserId != User.Identity.GetUserId())
						return StatusCode(HttpStatusCode.Forbidden);

					string root = Directory.CreateDirectory(HostingEnvironment.MapPath("~/App_Data/Import/" + Path.GetRandomFileName())).FullName;

					var provider = new MultipartFormDataStreamProvider(root);

					// Read the form data.
					await Request.Content.ReadAsMultipartAsync(provider);

					var fileGroups = provider.FileData.Select(file => new
					{
						name = Path.GetFileNameWithoutExtension(file.Headers.ContentDisposition.FileName.Trim('"')),
						local = file.LocalFileName,
						ext = Path.GetExtension(file.Headers.ContentDisposition.FileName.Trim('"'))
					})
						.GroupBy(item => item.name, (keys, items) => items.ToList())
						.Where(group => group.Any(file => file.ext == ".shp"))
						.ToArray();

					foreach (var group in fileGroups)
						foreach (var file in group)
							File.Move(file.local, group.First(file2 => file2.ext == ".shp").local + file.ext);

					var dbGeometries = fileGroups
						.Select(group => ((group.Any(file => file.ext == ".dbf"))
								? Helpers.ReadShape(group.First(file2 => file2.ext == ".shp").local, true)
								: Helpers.ReadShapeNoData(group.First(file2 => file2.ext == ".shp").local + ".shp", true))
							.Select(feature => DbGeometry.FromText(feature.Geometry.ToString())))
						.SelectMany(geo => geo)
						.ToList();

					if (!dbGeometries.Any())
						throw new System.Exception("No valid geometries have been found.");

					db.Cases.FirstOrDefault(c => c.Id == caseId).LastModified = System.DateTime.UtcNow;

					Helpers.RemoveCaseOutputAndResults(db, caseId, false);

					db.Exceptions.RemoveRange(db.Exceptions
						.Where(e => e.CaseId == caseId));

					await db.SaveChangesAsync();

					dbGeometries = dbGeometries.Distinct(new DbGeometryEqualityComparer()).ToList();

					db.Exceptions.AddRange(dbGeometries
						.Select(geo => new Exception() { CaseId = caseId, geo = geo }));

					await db.SaveChangesAsync();

					transaction.Commit();

					return Ok();
				}
				catch (System.Exception ex)
				{
					transaction.Rollback();
					throw ex;
				}
			}
		}

		/// <summary>
		/// Verwijder een uitzonderingsgebied.
		/// </summary>
		/// <param name="modelId">Id van het model.</param>
		/// <param name="projectId">Id van het project.</param>
		/// <param name="caseId">Id van het rekengeval.</param>
		/// <param name="id">Id van het uitzonderingsgebied.</param>
		/// <returns>Het verwijderde uitzonderingsgebied.</returns>
		[Route("{modelId}/Projects/{projectId}/Cases/{caseId}/Exceptions/{id}")]
		public async Task<IHttpActionResult> Delete(string modelId, int projectId, int caseId, int id)
		{
			using (var transaction = db.Database.BeginTransaction())
			{
				try
				{
					var exceptionInDb = db
						.Exceptions
						.Include(c => c.Case).Include(c => c.Case.Project)
						.Single(e =>
							e.Id == id &&
							e.Case.Id == caseId &&
							e.Case.Project.Id == projectId);

					if (exceptionInDb.Case.Project.UserId != User.Identity.GetUserId())
						return StatusCode(HttpStatusCode.Forbidden);

					exceptionInDb.Case.LastModified = System.DateTime.UtcNow;

					db.Exceptions.Remove(exceptionInDb);

					Helpers.RemoveCaseOutputAndResults(db, caseId, false);

					await db.SaveChangesAsync();

					transaction.Commit();

					return Ok(exceptionInDb);
				}
				catch (System.Exception ex)
				{
					transaction.Rollback();
					throw ex;
				}
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				db.Dispose();
			}
			base.Dispose(disposing);
		}

		private class DbGeometryEqualityComparer : IEqualityComparer<DbGeometry>
		{
			bool IEqualityComparer<DbGeometry>.Equals(DbGeometry a, DbGeometry b)
			{
				return a.SpatialEquals(b);
			}

			int IEqualityComparer<DbGeometry>.GetHashCode(DbGeometry a)
			{
				return 0;
			}
		}

		private static double[] res = new[] { 3440.640, 1720.320, 860.160, 430.080, 215.040, 107.520, 53.760, 26.880, 13.440, 6.720, 3.360, 1.680, 0.840, 0.420 };
	}
}