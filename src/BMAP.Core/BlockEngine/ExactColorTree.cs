using BMAP.ColorEngine;
using BMAP.Palette;

namespace BMAP.BlockEngine;

/// <summary>Exact k-d tree for squared Euclidean colour metrics. Equal distances retain palette preference order.</summary>
internal sealed class ExactColorTree
{
    private readonly Lab[] _points;
    private readonly int[] _bits;
    private readonly int[] _left, _right, _mask;
    private readonly byte[] _axis;
    private readonly int _root;

    public ExactColorTree(Lab[] points, int[] shadeBits)
    {
        _points = points; _bits = shadeBits;
        _left = new int[points.Length]; _right = new int[points.Length];
        _mask = new int[points.Length]; _axis = new byte[points.Length];
        int[] ids = Enumerable.Range(0,points.Length).ToArray();
        _root = Build(0,ids.Length);

        int Build(int start, int length)
        {
            if (length == 0) return -1;
            int axis = 0; double spread = -1;
            for (int a = 0; a < 3; a++)
            {
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                for (int i = start; i < start+length; i++)
                {
                    double v = Component(points[ids[i]],a);
                    min = Math.Min(min,v); max = Math.Max(max,v);
                }
                if (max-min > spread) { spread = max-min; axis = a; }
            }
            Array.Sort(ids,start,length,Comparer<int>.Create((a,b)=>
            {
                int cmp = Component(points[a],axis).CompareTo(Component(points[b],axis));
                return cmp == 0 ? a.CompareTo(b) : cmp;
            }));
            int mid = start+length/2, node = ids[mid];
            _axis[node] = (byte)axis;
            _left[node] = Build(start,mid-start);
            _right[node] = Build(mid+1,start+length-mid-1);
            _mask[node] = shadeBits[node] | (_left[node]<0 ? 0 : _mask[_left[node]]) | (_right[node]<0 ? 0 : _mask[_right[node]]);
            return node;
        }
    }

    public int Find(Lab target, int shadeMask, double penalty, double floor)
    {
        int best = -1;
        double distance = double.MaxValue;
        Search(_root);
        return best;

        void Search(int node)
        {
            if (node < 0 || (_mask[node] & shadeMask) == 0) return;
            var p = _points[node];
            if ((_bits[node] & shadeMask) != 0)
            {
                double dl = target.L-p.L, da = target.A-p.A, db = target.B-p.B;
                double d = dl*dl+da*da+db*db;
                if (penalty > 0 && _bits[node] != 1 << (int)Shade.Normal) d = d*(1+penalty)+penalty*floor;
                if (d < distance || d == distance && (best < 0 || node < best)) { distance = d; best = node; }
            }
            double plane = Component(target,_axis[node])-Component(p,_axis[node]);
            int near = plane <= 0 ? _left[node] : _right[node], far = plane <= 0 ? _right[node] : _left[node];
            Search(near);
            // A nonnegative shade penalty can only increase the distance. The plane is a conservative lower bound.
            // Include equality: an earlier palette entry may be tied on the opposite side.
            if (plane*plane <= distance) Search(far);
        }
    }

    private static double Component(Lab v, int axis) => axis switch { 0 => v.L, 1 => v.A, _ => v.B };
}
