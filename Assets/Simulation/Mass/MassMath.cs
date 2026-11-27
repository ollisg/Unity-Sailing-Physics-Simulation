using System;
using UnityEngine;

/// <summary>Symmetric 3x3 matrix (second moments, inertia tensors).</summary>
[Serializable]
public struct Sym3
{
    public float xx, yy, zz, xy, xz, yz;

    public Sym3(float xx, float yy, float zz, float xy, float xz, float yz)
    {
        this.xx = xx; this.yy = yy; this.zz = zz; this.xy = xy; this.xz = xz; this.yz = yz;
    }

    public static Sym3 Zero => default;
    public static Sym3 Identity(float s) => new Sym3(s, s, s, 0f, 0f, 0f);

    public float Trace => xx + yy + zz;

    /// <summary>v vᵀ</summary>
    public static Sym3 Outer(Vector3 v) =>
        new Sym3(v.x * v.x, v.y * v.y, v.z * v.z, v.x * v.y, v.x * v.z, v.y * v.z);

    /// <summary>a bᵀ + b aᵀ</summary>
    public static Sym3 Outer2(Vector3 a, Vector3 b) =>
        new Sym3(2f * a.x * b.x, 2f * a.y * b.y, 2f * a.z * b.z,
                 a.x * b.y + b.x * a.y, a.x * b.z + b.x * a.z, a.y * b.z + b.y * a.z);

    public static Sym3 operator +(Sym3 a, Sym3 b) =>
        new Sym3(a.xx + b.xx, a.yy + b.yy, a.zz + b.zz, a.xy + b.xy, a.xz + b.xz, a.yz + b.yz);

    public static Sym3 operator -(Sym3 a, Sym3 b) =>
        new Sym3(a.xx - b.xx, a.yy - b.yy, a.zz - b.zz, a.xy - b.xy, a.xz - b.xz, a.yz - b.yz);

    public static Sym3 operator *(Sym3 a, float s) =>
        new Sym3(a.xx * s, a.yy * s, a.zz * s, a.xy * s, a.xz * s, a.yz * s);

    /// <summary>R S Rᵀ: the same tensor expressed after rotating by q.</summary>
    public Sym3 Rotated(Quaternion q)
    {
        Vector3 cx = q * Vector3.right, cy = q * Vector3.up, cz = q * Vector3.forward;
        return Outer(cx) * xx + Outer(cy) * yy + Outer(cz) * zz
             + Outer2(cx, cy) * xy + Outer2(cx, cz) * xz + Outer2(cy, cz) * yz;
    }

    /// <summary>
    /// Eigen decomposition (cyclic Jacobi). Returns eigenvalues and a rotation whose
    /// local x/y/z axes are the matching eigenvectors (right-handed).
    /// </summary>
    public void Eigen(out Vector3 values, out Quaternion axes)
    {
        double[,] a = { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };

        for (int sweep = 0; sweep < 32; sweep++)
        {
            double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            double diag = a[0, 0] * a[0, 0] + a[1, 1] * a[1, 1] + a[2, 2] * a[2, 2];
            if (off <= 1e-20 * (diag + 1e-30)) break;

            for (int p = 0; p < 2; p++)
            for (int q = p + 1; q < 3; q++)
            {
                if (Math.Abs(a[p, q]) < 1e-30) continue;
                double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                double t = theta == 0.0 ? 1.0
                         : Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                double c = 1.0 / Math.Sqrt(t * t + 1.0), s = t * c;

                for (int k = 0; k < 3; k++)
                {
                    double akp = a[k, p], akq = a[k, q];
                    a[k, p] = c * akp - s * akq;
                    a[k, q] = s * akp + c * akq;
                }
                for (int k = 0; k < 3; k++)
                {
                    double apk = a[p, k], aqk = a[q, k];
                    a[p, k] = c * apk - s * aqk;
                    a[q, k] = s * apk + c * aqk;
                }
                for (int k = 0; k < 3; k++)
                {
                    double vkp = v[k, p], vkq = v[k, q];
                    v[k, p] = c * vkp - s * vkq;
                    v[k, q] = s * vkp + c * vkq;
                }
            }
        }

        values = new Vector3((float)a[0, 0], (float)a[1, 1], (float)a[2, 2]);
        Vector3 e0 = new Vector3((float)v[0, 0], (float)v[1, 0], (float)v[2, 0]);
        Vector3 e1 = new Vector3((float)v[0, 1], (float)v[1, 1], (float)v[2, 1]);
        Vector3 e2 = Vector3.Cross(e0, e1); // forces a right-handed basis
        axes = Quaternion.LookRotation(e2, e1);
    }
}

/// <summary>
/// Adds up masses in one reference frame (the boat's). Each piece is a mass at a centre
/// plus a "spread" tensor: the covariance of its mass around its own centre, in m².
/// From the totals it gives the combined centre of mass and the inertia tensor about it.
/// </summary>
public struct MassAccumulator
{
    public float mass;
    public Vector3 firstMoment;   // Σ m r
    public Sym3 secondMoment;     // Σ m (spread + r rᵀ), about the frame origin

    public void Add(float m, Vector3 centre, Sym3 spread)
    {
        if (m <= 0f) return;
        mass += m;
        firstMoment += centre * m;
        secondMoment += (spread + Sym3.Outer(centre)) * m;
    }

    public void Add(MassAccumulator other)
    {
        mass += other.mass;
        firstMoment += other.firstMoment;
        secondMoment += other.secondMoment;
    }

    public Vector3 CenterOfMass => mass > 0f ? firstMoment / mass : Vector3.zero;

    /// <summary>Inertia tensor about the combined centre of mass (kg·m²).</summary>
    public Sym3 InertiaAboutCenter()
    {
        if (mass <= 0f) return Sym3.Zero;
        Vector3 c = CenterOfMass;
        Sym3 j = secondMoment - Sym3.Outer(c) * mass;
        return Sym3.Identity(j.Trace) - j;
    }

    // ---- spreads of simple shapes (per unit mass, m²) ----

    /// <summary>Solid cylinder / rod along unit direction u.</summary>
    public static Sym3 RodSpread(Vector3 u, float length, float radius)
    {
        Sym3 along = Sym3.Outer(u);
        return along * (length * length / 12f) + (Sym3.Identity(1f) - along) * (radius * radius / 4f);
    }

    /// <summary>Solid box with the given size, rotated by rot.</summary>
    public static Sym3 BoxSpread(Vector3 size, Quaternion rot, float factor = 1f)
    {
        var local = new Sym3(size.x * size.x, size.y * size.y, size.z * size.z, 0f, 0f, 0f) * (factor / 12f);
        return local.Rotated(rot);
    }

    /// <summary>Flat triangle with its luff on one edge: height h along a, foot b along c.</summary>
    public static Sym3 TriangleSpread(Vector3 a, float h, Vector3 c, float b)
    {
        return Sym3.Outer(a) * (h * h / 18f) + Sym3.Outer(c) * (b * b / 18f);
    }
}
