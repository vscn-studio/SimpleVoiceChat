namespace SimpleVoiceChat.Server;

public sealed class VoiceSpatialIndex
{
    private const double MinimumPositionDeltaSquared = 0.0025d;
    private readonly int cellSize;
    private readonly Dictionary<Cell, HashSet<string>> membersByCell = new();
    private readonly Dictionary<string, Entry> entriesByUid = new(StringComparer.Ordinal);

    public VoiceSpatialIndex(int cellSize)
    {
        this.cellSize = Math.Max(4, cellSize);
    }

    public int Count => entriesByUid.Count;

    public void Update(string uid, double x, double y, double z)
    {
        UpdateIfMoved(uid, x, y, z);
    }

    internal bool UpdateIfMoved(string uid, double x, double y, double z)
    {
        Cell nextCell = GetCell(x, z);
        if (entriesByUid.TryGetValue(uid, out Entry current))
        {
            double dx = current.X - x;
            double dy = current.Y - y;
            double dz = current.Z - z;
            if (current.Cell == nextCell
                && dx * dx + dy * dy + dz * dz < MinimumPositionDeltaSquared)
            {
                return false;
            }
            if (current.Cell != nextCell)
            {
                RemoveFromCell(uid, current.Cell);
                AddToCell(uid, nextCell);
            }
        }
        else
        {
            AddToCell(uid, nextCell);
        }

        entriesByUid[uid] = new Entry(nextCell, x, y, z);
        return true;
    }

    public bool Remove(string uid)
    {
        if (!entriesByUid.Remove(uid, out Entry entry))
        {
            return false;
        }

        RemoveFromCell(uid, entry.Cell);
        return true;
    }

    public void Query(double x, double y, double z, double radius, List<VoiceSpatialCandidate> destination)
    {
        destination.Clear();
        double safeRadius = Math.Max(0, radius);
        double radiusSquared = safeRadius * safeRadius;
        int minX = FloorCell(x - safeRadius);
        int maxX = FloorCell(x + safeRadius);
        int minZ = FloorCell(z - safeRadius);
        int maxZ = FloorCell(z + safeRadius);

        for (int cellX = minX; cellX <= maxX; cellX++)
        {
            for (int cellZ = minZ; cellZ <= maxZ; cellZ++)
            {
                if (!membersByCell.TryGetValue(new Cell(cellX, cellZ), out HashSet<string>? members))
                {
                    continue;
                }

                foreach (string uid in members)
                {
                    Entry entry = entriesByUid[uid];
                    double dx = entry.X - x;
                    double dy = entry.Y - y;
                    double dz = entry.Z - z;
                    double distanceSquared = dx * dx + dy * dy + dz * dz;
                    if (distanceSquared <= radiusSquared)
                    {
                        destination.Add(new VoiceSpatialCandidate(uid, distanceSquared));
                    }
                }
            }
        }
    }

    private Cell GetCell(double x, double z)
    {
        return new Cell(FloorCell(x), FloorCell(z));
    }

    private int FloorCell(double value)
    {
        return (int)Math.Floor(value / cellSize);
    }

    private void AddToCell(string uid, Cell cell)
    {
        if (!membersByCell.TryGetValue(cell, out HashSet<string>? members))
        {
            members = new HashSet<string>(StringComparer.Ordinal);
            membersByCell[cell] = members;
        }
        members.Add(uid);
    }

    private void RemoveFromCell(string uid, Cell cell)
    {
        if (!membersByCell.TryGetValue(cell, out HashSet<string>? members))
        {
            return;
        }

        members.Remove(uid);
        if (members.Count == 0)
        {
            membersByCell.Remove(cell);
        }
    }

    private readonly record struct Cell(int X, int Z);
    private readonly record struct Entry(Cell Cell, double X, double Y, double Z);
}

public readonly record struct VoiceSpatialCandidate(string PlayerUid, double DistanceSquared);
