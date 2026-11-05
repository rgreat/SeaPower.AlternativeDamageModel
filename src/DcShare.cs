using System;

namespace AlternativeDamageModel
{
    /// <summary>
    /// How Auto DC shares its free teams out among the jobs (pure arithmetic; DcAuto builds the jobs, in the order of their priority):
    ///  - one team: the first job; two or more: they are divided between the two jobs of the highest priority (half each, the first one gets the odd team);
    ///  - a job takes no more than it wants (Want); what a job cannot take goes to the next ones in the order of priority;
    ///  - a job of priority systems repair takes no more than a third of all the teams (rounded up) while there is fire or flooding work, and is never left
    ///    without a team when there are two teams or more (Bonus: its first slot is a fire fighter for a burning section, not a systems repairer).
    /// </summary>
    static class DcShare
    {
        public const double SystemsShare = 0.33;

        public sealed class Job
        {
            public int Want;       // teams the job asks for (the fire fighter of a burning section included)
            public int Bonus;      // 1 = the first slot is for the fire (does not count against the systems share)
            public bool Systems;   // a priority systems repair
        }

        /// <summary>The teams for each job. left: free teams to share; total: all the ship's teams; otherWork: fire or flooding work exists.</summary>
        public static int[] Run(Job[] jobs, int left, int total, bool otherWork)
        {
            int m = jobs.Length;
            var alloc = new int[m];
            if (m == 0 || left <= 0) return alloc;
            int teams = left;
            int cap = otherWork ? Math.Max(1, (int)Math.Ceiling(total * SystemsShare - 1e-9)) : int.MaxValue;

            Func<int> used = () =>
            {
                int u = 0;
                for (int k = 0; k < m; k++) if (jobs[k].Systems) u += Math.Max(0, alloc[k] - jobs[k].Bonus);
                return u;
            };
            Func<int, int> room = k =>
            {
                int r = jobs[k].Want - alloc[k];
                if (jobs[k].Systems && cap != int.MaxValue)
                {
                    int free = Math.Max(0, cap - used());
                    r = Math.Min(r, jobs[k].Bonus + Math.Max(0, alloc[k] - jobs[k].Bonus) + free - alloc[k]);
                }
                return Math.Max(0, r);
            };
            Func<int, int, int> give = (k, n) =>
            {
                n = Math.Min(Math.Min(n, room(k)), left);
                alloc[k] += n; left -= n;
                return n;
            };

            if (teams == 1) give(0, 1);
            else
            {
                int a = (teams + 1) / 2;
                give(0, a);
                if (m > 1) give(1, teams - a);
            }
            for (int k = 0; k < m && left > 0; k++) give(k, jobs[k].Want);

            // never fewer than one team on a priority systems repair (when there is more than one team to share)
            if (teams >= 2 && used() == 0)
            {
                int s = -1;
                for (int k = 0; k < m; k++) if (jobs[k].Systems) { s = k; break; }
                if (s >= 0)
                {
                    int need = Math.Min(jobs[s].Want, jobs[s].Bonus + 1);
                    for (int j = m - 1; j >= 0 && alloc[s] < need; j--)
                        while (j != s && alloc[j] > 0 && alloc[s] < need && !jobs[j].Systems) { alloc[j]--; alloc[s]++; }
                }
            }
            return alloc;
        }
    }
}
